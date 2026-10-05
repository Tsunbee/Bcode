// Monaco setup, tab model management, F12 entity-jump, Ctrl+G/Ctrl+Shift+O outline —
// all reimplemented here in JS rather than shared with Bcode.App's C# ScriptEditorControl:
// Monaco already has its own text engine, so porting the RTF-highlighter approach makes no
// sense, and file I/O goes through window.chrome.webview.hostObjects.host (see MainForm.cs's
// EditorBridge) instead of a direct filesystem call, since page JS can't touch arbitrary
// paths on its own.

/// Chạy fn lúc trình duyệt RẢNH (requestIdleCallback) và trả về hàm huỷ. Dùng cho việc nền chạy sau khi ngừng gõ
/// (kiểm tra lỗi, Outline, tô mục hay dùng): có phím mới thì huỷ lượt đang chờ chứ không chen vào giữa lúc gõ.
/// timeout: tối đa chờ bao lâu thì vẫn phải chạy dù trình duyệt chưa rảnh.
window.bcodeIdle = function (fn, timeout) {
  if (window.requestIdleCallback) {
    const id = window.requestIdleCallback(fn, { timeout: timeout || 2000 });
    return () => window.cancelIdleCallback(id);
  }
  const id = setTimeout(fn, 50);
  return () => clearTimeout(id);
};

// Tên entity có thể có dấu chấm/gạch/$ (vd `% Control.Unit`, `Sign.Function.Code`) — regex cũ chỉ nhận
// [A-Za-z0-9_] nên các khai báo đó không bao giờ được kiểm tra file tồn tại.
const ENTITY_DECL_RE = /<!ENTITY\s+%?\s*([A-Za-z0-9_.:$-]+)\s+SYSTEM\s+"([^"]+)"/g;
const WORD_RE = /[A-Za-z0-9_]/;

function detectLanguage(path, content) {
  const ext = path.slice(path.lastIndexOf('.')).toLowerCase();
  switch (ext) {
    case '.xml': case '.f': case '.ent': case '.txt':
      if (!window.bcodeFcodeLanguageReady) return 'xml';
      // .txt = file include; thường là 1 mảnh JS/SQL bọc CDATA → chọn biến thể có màu theo nội dung.
      return ext === '.txt' && window.detectFcodeVariantLanguage
        ? window.detectFcodeVariantLanguage(content)
        : window.FCODE_LANGUAGE_ID;
    case '.sql': return 'sql';
    case '.js': return 'javascript';
    case '.aspx': case '.html': return 'html';
    case '.json': return 'json';
    default: return 'plaintext';
  }
}


function buildSymbols(text) {
  const symbols = [];
  const push = (name, kind, index) => {
    const pos = offsetToPosition(text, index);
    const range = { startLineNumber: pos.line, startColumn: pos.col, endLineNumber: pos.line, endColumn: pos.col + name.length };
    symbols.push({ name, kind, range, selectionRange: range });
  };

  let m;
  const fieldRe = /<field\s+name="([^"]+)"/g;
  while ((m = fieldRe.exec(text))) push(m[1], monaco.languages.SymbolKind.Field, m.index);

  const viewRe = /<view\s+id="([^"]+)"/g;
  while ((m = viewRe.exec(text))) push(m[1], monaco.languages.SymbolKind.Class, m.index);

  const commandRe = /<command\s+event="([^"]+)"/g;
  while ((m = commandRe.exec(text))) push(m[1], monaco.languages.SymbolKind.Event, m.index);

  const funcRe = /function\s+([A-Za-z0-9_$]+)\s*\(/g;
  while ((m = funcRe.exec(text))) push(m[1], monaco.languages.SymbolKind.Function, m.index);

  const actionRe = /<action\s+id="([^"]+)"/g;
  while ((m = actionRe.exec(text))) push(m[1], monaco.languages.SymbolKind.Method, m.index);

  const entRe = /<!ENTITY\s+%?\s*([A-Za-z0-9_.]+)/g;
  while ((m = entRe.exec(text))) push(m[1], monaco.languages.SymbolKind.Constant, m.index);

  return symbols;
}

function offsetToPosition(text, offset) {
  const upTo = text.slice(0, offset);
  const line = (upTo.match(/\n/g) || []).length + 1;
  const lastNewline = upTo.lastIndexOf('\n');
  const col = offset - lastNewline;
  return { line, col };
}

class BcodeEditor {
  constructor(containerId) {
    this.docs = new Map(); // insertion order == tab order
    this.activePath = null;
    this.mru = [];
    this._validateTimer = null;
    document.addEventListener('keydown', (e) => {
      if (e.ctrlKey && e.code === 'KeyP') {
        e.preventDefault();
      }
    }, { capture: true });

    this.editorSecondary = null;
    this.secondaryPath = null;

    setInterval(() => this.checkExternalChange(), 4000);

    this.editor = monaco.editor.create(document.getElementById(containerId), {

      theme: window.bcodeTheme ? window.bcodeTheme.monacoThemeName : 'vs-dark',
      automaticLayout: true,
      fontFamily: window.bcodeTheme ? window.bcodeTheme.fontFamily : "'Roboto', Consolas, monospace",
      fontSize: 15,
      minimap: { enabled: true },
      mouseWheelZoom: true, // Ctrl + lăn chuột để phóng to/thu nhỏ chữ nhanh
      // Tô màu cặp ngoặc theo cấp lồng nhau như VS Code (mặc định bật ở VS Code); màu lấy từ
      // editorBracketHighlight.* của theme (theme nhập từ VS Code mang sẵn các màu này).
      bracketPairColorization: { enabled: true },
      glyphMargin: true // needed for the Bookmark gutter dot — see toggleBookmark
    });


    if (document.fonts && document.fonts.load) {
      document.fonts.load("15px 'Roboto'").catch(() => {}).then(() => monaco.editor.remeasureFonts());
    }

    this._cursorFrame = 0;
    this.editor.onDidChangeCursorPosition((e) => {
      this._pendingCursor = e.position;
      if (this._cursorFrame) return;
      this._cursorFrame = requestAnimationFrame(() => {
        this._cursorFrame = 0;
        const p = this._pendingCursor;
        window.chrome.webview.hostObjects.host.NotifyCursorChanged(p.lineNumber, p.column);
      });
    });

    monaco.languages.registerDocumentSymbolProvider(['xml', 'fcode-xml', 'fcode-js', 'fcode-sql'], {
      provideDocumentSymbols: (model) => buildSymbols(model.getValue())
    });

    this.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => this.saveActive());
    // F12 = xem trước (peek), Ctrl+F12 = đi tới file / nơi khai báo — xem entity.js goToOrPeek.
    this.editor.addCommand(monaco.KeyCode.F12, () => this.jumpToEntityAtCaret('peek'));
    this.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.F12, () => this.jumpToEntityAtCaret('go'));
    this.editor.addCommand(monaco.KeyCode.F11, () => this.gotoFunctionAtCaret());
    // Ctrl+G — hộp Go to (thay cho "đi tới dòng" mặc định của Monaco): tag + chi tiết, nhấp đúp để nhảy.
    this.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyG, () => window.bcodeGoto && window.bcodeGoto.open());
    // Alt+P — xem trước màn hình Dir (panel bên phải, tự cập nhật khi sửa file).
    this.editor.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.KeyP, () => this.toggleDirPreview());
    // Ctrl+I — lightweight version of VSCode Copilot's inline generate (see
    // BcodeDialogs.showInlineGenerate in contextmenu.js): asks Claude for code based on
    // a short instruction, shown for review before an explicit "Insert" applies it.
    this.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyI, () => window.bcodeDialogs.showInlineGenerate(this));
    // F5 — lưu file rồi mở menu của file này bằng trình duyệt mặc định (xem saveAndRunMenu).
    // Bắt ở cấp document (pha capture) chứ không chỉ khi con trỏ đang trong editor: nếu F5 xuất hiện lúc focus ở chỗ
    // khác trong trang (panel, ô chat...) thì trình duyệt sẽ tải lại cả trang.
    document.addEventListener('keydown', (e) => {
      if (e.key !== 'F5' || e.ctrlKey || e.altKey || e.shiftKey || e.metaKey) return;
      e.preventDefault();
      e.stopPropagation();
      if (!e.repeat) this.saveAndRunMenu();
    }, true);
    // Ctrl+Alt+T — dịch caption <header v e> (xem BcodeDialogs.showTranslateHeaders).
    this.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Alt | monaco.KeyCode.KeyT, () => window.bcodeDialogs.showTranslateHeaders(this));

    // Ctrl+D — nhân đôi dòng hiện tại (hoặc các dòng đang bôi đen).
    //
    // This deliberately takes Ctrl+D away from Monaco's default, which is VSCode's "add
    // the next occurrence to the selection" (multi-cursor). Ctrl+D as duplicate-line is
    // what JetBrains and several other editors use, and it's what was asked for — but
    // losing multi-cursor entirely would be a real loss, so that action moves to
    // Ctrl+Shift+D rather than disappearing. Monaco's own Ctrl+Shift+L ("select all
    // occurrences") is untouched and still does the whole-file version.
    //
    // Registered with addAction rather than addCommand so both show up in Monaco's
    // command palette (F1) with a name, instead of being invisible key bindings.
    this.editor.addAction({
      id: 'bcode.duplicateLine',
      label: 'Nhân đôi dòng',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyD],
      run: (ed) => {
        const model = ed.getModel();
        const selections = ed.getSelections() || [];
        // Không bôi đen gì → nhân đôi cả dòng như cũ.
        if (!model || !selections.some((s) => !s.isEmpty())) {
          ed.getAction('editor.action.copyLinesDownAction')?.run();
          return;
        }
        // Có bôi đen → chỉ chép ĐOẠN text đó, chèn ngay sau nó rồi bôi đen bản mới (bấm tiếp Ctrl+D
        // thì chép tiếp). Con trỏ không bôi đen (khi đang nhiều con trỏ) thì giữ nguyên.
        const edits = [];
        const texts = [];
        for (const s of selections) {
          if (s.isEmpty()) { texts.push(null); continue; }
          const text = model.getValueInRange(s);
          texts.push(text);
          edits.push({ range: new monaco.Range(s.endLineNumber, s.endColumn, s.endLineNumber, s.endColumn), text, forceMoveMarkers: true });
        }
        ed.pushUndoStop();
        ed.executeEdits('bcode.duplicateSelection', edits, (inverse) => {
          // inverse[i] = vùng vừa chèn (sau khi sửa) theo thứ tự chỉnh sửa; dựng lại selection từ đó.
          // Thứ tự của inverse không đảm bảo trùng thứ tự đầu vào → sắp theo vị trí rồi ghép.
          const byPos = (a, b) => a.startLineNumber - b.startLineNumber || a.startColumn - b.startColumn;
          const inserted = inverse.map((x) => x.range).sort(byPos);
          return inserted.map((r) => new monaco.Selection(r.startLineNumber, r.startColumn, r.endLineNumber, r.endColumn));
        });
        ed.pushUndoStop();
      },
    });
    this.editor.addAction({
      id: 'bcode.addSelectionToNextMatch',
      label: 'Thêm con trỏ ở kết quả giống tiếp theo',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyD],
      run: (ed) => ed.getAction('editor.action.addSelectionToNextFindMatch')?.run(),
    });

    // ---- Panels and tabs ----------------------------------------------------------
    // All registered as actions so they are reachable from the command palette (F1) by
    // name as well as by key — the keys below collide with nothing Monaco binds.
    this.editor.addAction({
      id: 'bcode.findInFiles',
      label: 'Tìm trong toàn bộ project',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyF],
      run: () => window.bcodeSearch.open(this.editor.getModel()?.getValueInRange(this.editor.getSelection()) || ''),
    });
    this.editor.addAction({
      id: 'bcode.toggleProblems',
      label: 'Hiện/ẩn bảng Problems',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyM],
      run: () => window.bcodeProblems.toggle(),
    });
    this.editor.addAction({
      id: 'bcode.toggleOutline',
      label: 'Hiện/ẩn Outline',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyU],
      run: () => window.bcodeOutline.toggle(),
    });
    this.editor.addAction({
      id: 'bcode.findReferences',
      label: 'Tìm nơi sử dụng (toàn project)',
      keybindings: [monaco.KeyMod.Shift | monaco.KeyCode.F12],
      run: () => window.bcodeOutline.findReferencesAtCaret(),
    });
    // Ctrl+Enter — chạy câu SQL tại con trỏ. Monaco binds nothing to it, and it is what
    // SSMS/Azure Data Studio use for the same action.
    this.editor.addAction({
      id: 'bcode.runSql',
      label: 'Chạy SQL tại con trỏ',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter],
      run: () => window.bcodeSqlRun.run(),
    });
    this.editor.addAction({
      id: 'bcode.toggleSplit',
      label: 'Tách đôi khung soạn thảo',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Backslash],
      run: () => this.toggleSplit(),
    });
    this.editor.addAction({
      id: 'bcode.closeTab',
      label: 'Đóng tab hiện tại',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyW],
      run: () => this.closeActive(),
    });
    // Ctrl+Tab / Ctrl+Shift+Tab. Monaco normally treats Tab as an editing key; binding it
    // with Ctrl held is unambiguous, and this is the switcher people reach for first.
    this.editor.addAction({
      id: 'bcode.nextTab',
      label: 'Tab kế tiếp',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Tab],
      run: () => this.cycleTab(1),
    });
    this.editor.addAction({
      id: 'bcode.prevTab',
      label: 'Tab trước đó',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.Tab],
      run: () => this.cycleTab(-1),
    });

    this.editor.addAction({
      id: 'bcode.fileHistory',
      label: 'Lịch sử file (các bản đã lưu)',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyH],
      run: () => window.bcodeHistory.showHistory(this),
    });

    // FCode's own right-click menu (Goto Response Tag/Command/Function, Open Folder,
    // Create Function, Lookup Regex, Convert to XML, Refresh) is a custom menu, not
    // Monaco's built-in one (which can't do the nested "Open Folder >" submenu) — see
    // contextmenu.js. contextmenu:false stops Monaco's own from showing underneath it.
    //
    // Uses Monaco's own editor.onContextMenu API rather than a raw DOM 'contextmenu'
    // listener — a first attempt at the latter (even in the capture phase) never fired:
    // Monaco's mouse handling swallows the native event internally before it's usable from
    // outside. onContextMenu is Monaco's own documented hook for exactly this ("replace my
    // built-in menu with your own"), firing with the position already resolved.
    this.editor.updateOptions({ contextmenu: false });
    this.editor.onContextMenu((e) => {
      e.event.preventDefault();
      window.bcodeContextMenu.show(e.event.posx, e.event.posy, this);
    });

    this.editor.onDidChangeModelContent(() => {
      if (!this.activePath) return;
      // Dirty = "differs from the last saved version", not "edited since": Monaco's
      // alternative version id returns to the saved value when Ctrl+Z walks back to it, so
      // undoing every edit clears the • on the tab and in the tree again.
      this.updateDirty(this.activePath);
      // Re-run the same checks FCodeViewer itself runs (missing ENTITY file, duplicate
      // field names) as the user types, not just on open — debounced so a fast typist
      // doesn't trigger a PathExists round-trip per keystroke.
      // Ngừng gõ ~0,7s mới hẹn, rồi chờ trình duyệt rảnh; gõ tiếp thì huỷ cả hai. Phần nặng nằm trong worker.
      clearTimeout(this._validateTimer);
      if (this._cancelValidateIdle) this._cancelValidateIdle();
      this._validateTimer = setTimeout(() => { this._cancelValidateIdle = window.bcodeIdle(() => this.validateActive(), 3000); }, 700);
      // The tab's dirty marker and the outline both follow the text, on the same debounce
      // budget — rebuilding a symbol tree per keystroke is the one thing here big enough
      // to be felt on a 4000-line controller.
      if (window.bcodeTabs) window.bcodeTabs.render();
      clearTimeout(this._outlineTimer);
      if (this._cancelOutlineIdle) this._cancelOutlineIdle();
      this._outlineTimer = setTimeout(() => {
        this._cancelOutlineIdle = window.bcodeIdle(() => window.bcodeOutline && window.bcodeOutline.refresh(), 4000);
      }, 1000);
    });

    this.editor.onDidChangeCursorPosition(() => {
      if (window.bcodeOutline) window.bcodeOutline.highlightCaret();
    });
  }

  // ---- Per-document state ------------------------------------------------------------
  // These forward to the active document's entry in this.docs. They exist so that the
  // dozens of places already written as `this.currentModel` / `this.dirty` / `this.bookmarks`
  // did not each have to learn about tabs; only the handful of methods that create, switch
  // or destroy a document know the map is there.

  get activeDoc() { return this.activePath ? this.docs.get(this.activePath) : null; }

  /// Records the model's current version (or <paramref>versionId</paramref>) as the one
  /// matching disk — just opened, saved or reloaded — and re-derives the dirty flag.
  markClean(path, versionId) {
    const doc = this.docs.get(path);
    if (!doc) return;
    doc.savedVersionId = versionId ?? doc.model.getAlternativeVersionId();
    this.updateDirty(path);
  }

  /// Dirty flag from the model version; the host is told only when it actually flips, so
  /// typing doesn't cost a host round-trip per keystroke.
  updateDirty(path) {
    const doc = this.docs.get(path);
    if (!doc) return;
    const dirty = doc.model.getAlternativeVersionId() !== doc.savedVersionId;
    if (dirty === doc.dirty) return;
    doc.dirty = dirty;
    window.chrome.webview.hostObjects.host.NotifyDirtyChanged(path, dirty);
    if (window.bcodeTabs) window.bcodeTabs.render();
  }

  /// Unsaved open files with their current text — read synchronously by the host while the
  /// window is closing, to ask about them and, on "Yes", write them itself.
  getDirtyDocs() {
    const out = [];
    for (const [path, doc] of this.docs) if (doc.dirty) out.push({ path, content: doc.model.getValue() });
    return out;
  }

  get currentModel() { const d = this.activeDoc; return d ? d.model : null; }
  get dirty() { const d = this.activeDoc; return d ? d.dirty : false; }
  set dirty(v) { const d = this.activeDoc; if (d) d.dirty = v; }
  get bookmarks() { const d = this.activeDoc; return d ? d.bookmarks : new Set(); }
  set bookmarks(v) { const d = this.activeDoc; if (d) d.bookmarks = v; }
  get bookmarkDecorations() { const d = this.activeDoc; return d ? d.bookmarkDecorations : []; }
  set bookmarkDecorations(v) { const d = this.activeDoc; if (d) d.bookmarkDecorations = v; }
  get loadedWriteTimeUtc() { const d = this.activeDoc; return d ? d.loadedWriteTimeUtc : null; }
  set loadedWriteTimeUtc(v) { const d = this.activeDoc; if (d) d.loadedWriteTimeUtc = v; }
  get dismissedWriteTimeUtc() { const d = this.activeDoc; return d ? d.dismissedWriteTimeUtc : null; }
  set dismissedWriteTimeUtc(v) { const d = this.activeDoc; if (d) d.dismissedWriteTimeUtc = v; }

  /// Tab order, i.e. the map's own insertion order.
  openPaths() { return [...this.docs.keys()]; }

  /// Moves <paramref>path</paramref> to the top of the MRU stack.
  touchMru(path) {
    const i = this.mru.indexOf(path);
    if (i >= 0) this.mru.splice(i, 1);
    this.mru.push(path);
  }

  /// Ctrl+Tab. Walks tab order rather than the MRU stack: a true MRU switcher needs the
  /// "while Ctrl is still held" state that a web page cannot observe reliably, and a
  /// two-item MRU toggle that silently becomes a ping-pong is worse than a predictable
  /// left-to-right cycle.
  cycleTab(delta) {
    const paths = this.openPaths();
    if (paths.length < 2) return;
    const i = paths.indexOf(this.activePath);
    const next = paths[(i + delta + paths.length) % paths.length];
    this.activateDoc(next);
  }

  /// Shows an already-open document. Everything that differs per file — the model, the
  /// caret/scroll/fold state, bookmarks, the stale-file banner, the problem list, the
  /// outline — is swapped here, in one place.
  activateDoc(path) {
    const doc = this.docs.get(path);
    if (!doc || this.activePath === path) return;

    // Hand the outgoing document its scroll position and folds back, or every tab switch
    // would return to line 1.
    const prev = this.activeDoc;
    if (prev) prev.viewState = this.editor.saveViewState();

    this.activePath = path;
    this.touchMru(path);
    this.editor.setModel(doc.model);
    if (doc.viewState) this.editor.restoreViewState(doc.viewState);
    this.editor.focus();
    this.renderBookmarks();

    // Monaco raises no cursor event for a model swap, so the status bar would go on showing
    // the line and column of the tab you just left.
    const pos = this.editor.getPosition();
    if (pos) window.chrome.webview.hostObjects.host.NotifyCursorChanged(pos.lineNumber, pos.column);

    this.hideExternalChangeBanner();
    window.chrome.webview.hostObjects.host.NotifyFileOpened(path);

    if (window.bcodeTabs) window.bcodeTabs.render();
    if (window.bcodeOutline) window.bcodeOutline.refresh();
    this.validateActive();
    // Warms the entity index for this document so completion can offer the names that come
    // from its included files. Fire-and-forget: the provider falls back to the document's
    // own declarations until the walk lands.
    if (window.bcodeEntity) window.bcodeEntity.refreshIncludeIndex(path, doc.model.getValue());
    // A file that went stale while it sat in the background gets its banner now rather
    // than up to four seconds later.
    this.checkExternalChange();
  }

  // ---- Split view ---------------------------------------------------------------------

  /// Opens (or closes) a second editor to the right. Both sides bind to models from the
  /// same this.docs map, so showing one file in both panes is two views of ONE model:
  /// typing in either shows up in the other immediately, which is the point of splitting a
  /// 4000-line controller — the <field> declarations up top and the function that handles
  /// them 3000 lines down, on screen together.
  toggleSplit() {
    if (this.editorSecondary) { this.closeSplit(); return; }
    if (!this.activePath) return;

    document.getElementById('splitDivider').style.display = 'block';
    document.getElementById('editorSecondaryPane').style.display = 'flex';

    this.editorSecondary = monaco.editor.create(document.getElementById('editorContainerSecondary'), {
      theme: window.bcodeTheme ? window.bcodeTheme.monacoThemeName : 'vs-dark',
      automaticLayout: true,
      fontFamily: window.bcodeTheme ? window.bcodeTheme.fontFamily : "'Roboto', Consolas, monospace",
      fontSize: 15,
      minimap: { enabled: false }, // narrow by definition; the minimap costs more width than it earns here
      bracketPairColorization: { enabled: true },
      glyphMargin: true,
    });
    this.showInSplit(this.activePath);

    document.getElementById('secondaryCloseBtn').onclick = () => this.closeSplit();
    document.getElementById('secondaryFilePicker').onchange = (e) => this.showInSplit(e.target.value);
    this.setupSplitDivider();
    this.refreshSplitPicker();
  }

  closeSplit() {
    if (!this.editorSecondary) return;
    // setModel(null) first: disposing an editor that still holds a model shared with the
    // main pane must not take that model with it.
    this.editorSecondary.setModel(null);
    this.editorSecondary.dispose();
    this.editorSecondary = null;
    this.secondaryPath = null;
    document.getElementById('splitDivider').style.display = 'none';
    document.getElementById('editorSecondaryPane').style.display = 'none';
  }

  showInSplit(path) {
    const doc = this.docs.get(path);
    if (!this.editorSecondary || !doc) return;
    this.secondaryPath = path;
    this.editorSecondary.setModel(doc.model);
    this.refreshSplitPicker();
  }

  /// Keeps the split pane's file dropdown in step with the open tabs — called on every
  /// open/close so a file closed in the main pane can't stay selectable here.
  refreshSplitPicker() {
    const picker = document.getElementById('secondaryFilePicker');
    if (!picker || !this.editorSecondary) return;
    picker.innerHTML = '';
    for (const path of this.openPaths()) {
      const opt = document.createElement('option');
      opt.value = path;
      opt.textContent = fileNameOf(path);
      opt.title = path;
      if (path === this.secondaryPath) opt.selected = true;
      picker.appendChild(opt);
    }
  }

  setupSplitDivider() {
    const divider = document.getElementById('splitDivider');
    const pane = document.getElementById('editorSecondaryPane');
    const area = document.getElementById('editorArea');
    if (divider._wired) return;
    divider._wired = true;
    divider.addEventListener('mousedown', (down) => {
      down.preventDefault();
      // Captured on the document, not on the divider: a fast drag outruns a 5px-wide
      // element and the pane would stop following the cursor mid-gesture.
      const onMove = (e) => {
        const rect = area.getBoundingClientRect();
        const width = Math.min(Math.max(rect.right - e.clientX, 160), rect.width - 200);
        pane.style.width = width + 'px';
      };
      const onUp = () => {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
      };
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });
  }

  /// Runs the document's checks and hands the result to the Problems panel (problems.js,
  /// which owns the rules themselves). Debounced from the content-change handler.
  ///
  /// This used to render red bars stacked above the editor. They were dismissible, one per
  /// problem, and there was no way back to a dismissed one and no count anywhere — so a
  /// file with six issues either buried the editor or, after one ✕ each, looked clean. The
  /// panel keeps the list addressable and adds squiggles in the text itself.
  async validateActive() {
    if (window.bcodeProblems) await window.bcodeProblems.validate();
  }

  /// A problem row's click target: a missing-entity-file error has nothing to jump to
  /// *inside* this document (the problem is the external file itself, at `item.path`), so
  /// it opens that path the same way double-clicking it in the file tree would; anything
  /// with an in-document location instead moves the caret there and reveals it.
  goToValidationIssue(item) {
    if (item.path) {
      this.openFile(item.path, { line: item.line, column: item.column });
      return;
    }
    if (item.line != null) this.revealPosition(item.line, item.column || 1);
  }

  /// Kept as the one entry point the rest of the app uses to clear/replace the problem
  /// list, so callers (closeDoc and friends) don't need to know where it is rendered.
  setValidationBanners(items) {
    if (window.bcodeProblems) window.bcodeProblems.setItems(items);
  }

  /// Opens <paramref>path</paramref> in a tab of its own and makes it active. A file that
  /// is already open is simply brought forward — with its caret, scroll position and undo
  /// history intact, which is the whole reason the tab exists.
  ///
  /// Nothing is discarded here any more: unsaved work in another tab stays in that tab, so
  /// the "open another file and lose your changes?" prompt this used to show is gone. The
  /// prompt now lives where the loss actually happens — closing (see closeDoc).
  ///
  /// <paramref>opts.line</paramref>/<paramref>opts.column</paramref>, when given, reveal
  /// that position after opening — used by the search results and reference lists.
  async openFile(path, opts) {
    if (this.docs.has(path)) {
      this.activateDoc(path);
      if (opts && opts.line) this.revealPosition(opts.line, opts.column || 1);
      return;
    }

    let content;
    try {
      content = await window.bcodeHost.call('BeginReadFile', path);
    } catch (e) {
      alert('Không đọc được file:\n' + path + '\n' + e);
      return;
    }

    let writeTime = null;
    try { writeTime = await window.bcodeHost.call('BeginGetFileWriteTimeUtc', path); }
    catch { /* unreadable mtime just means one extra poll before the watch settles */ }

    // Between the await above and here another open of the same path may have completed
    // (double-click, or a search result clicked twice). Creating a second model for one
    // file would leave two tabs editing the same bytes.
    if (this.docs.has(path)) { this.activateDoc(path); return; }

    const model = monaco.editor.createModel(content, detectLanguage(path, content));
    this.docs.set(path, {
      model,
      dirty: false,
      savedVersionId: model.getAlternativeVersionId(),
      bookmarks: new Set(), // bookmarks are per file and are not persisted
      bookmarkDecorations: [],
      viewState: null,
      loadedWriteTimeUtc: writeTime,
      dismissedWriteTimeUtc: null,
    });

    // activateDoc, not a manual switch: it is the only place that remembers to hand the
    // outgoing document its scroll position and folds back before swapping models.
    this.activateDoc(path);

    if (opts && opts.line) this.revealPosition(opts.line, opts.column || 1);
    this.refreshSplitPicker();
  }

  /// Moves the caret and scrolls to it — the landing step shared by every "go to" in the
  /// panels (search hit, problem, reference, outline node).
  revealPosition(line, column) {
    this.editor.revealLineInCenter(line);
    this.editor.setPosition({ lineNumber: line, column: column || 1 });
    this.editor.focus();
  }

  /// Actually closes the open document: clears the editor and drops every piece of state
  /// tied to that file.
  ///
  /// Until this existed, the ✕ on a row in the left tree only removed the entry from the
  /// recent-files list — the file stayed loaded, stayed editable, and stayed the target of
  /// Ctrl+S, while the 4-second external-change poll kept running against it. "Closed" in
  /// the tree and "open" in the editor disagreeing is what produced the reports of a closed
  /// file still being on screen.
  ///
  /// Returns false when the user cancels at the unsaved-changes prompt, so the caller can
  /// leave the tree entry alone rather than removing a row for a file that is still open.
  closeActive(force) {
    if (!this.activePath) return true;
    return this.closeDoc(this.activePath, force);
  }

  /// Closes one tab — the ✕ on it, Ctrl+W, or the host removing the row from the left
  /// tree. Returns false when the user cancels at the unsaved-changes prompt, so the
  /// caller can leave the tree entry alone rather than removing a row for a file that is
  /// still open.
  ///
  /// The prompt is only asked here, because this is the only point where unsaved text
  /// actually stops existing; switching tabs no longer risks anything.
  async closeDoc(path, force) {
    const doc = this.docs.get(path);
    if (!doc) return true;
    if (!force && doc.dirty &&
        !(await window.bcodeUi.confirm(`"${fileNameOf(path)}" có thay đổi chưa lưu. Đóng và bỏ thay đổi?`, { okText: "Đóng, bỏ thay đổi", danger: true }))) {
      return false;
    }

    const wasActive = this.activePath === path;

    // File kế bên TRONG CÙNG NHÁNH của cây (vd 2 file trong "Grid"): đóng 1 file thì sang file còn lại của nhánh đó, hết file trong nhánh mới nhảy sang
    // nhánh khác. Tính trước khi xoá khỏi docs để biết vị trí của file đang đóng trong nhánh.
    let branchNext = null;
    if (wasActive && window.bcodeShell && window.bcodeShell.branchFiles) {
      const branch = window.bcodeShell.branchFiles(path), at = branch.findIndex((p) => p.toLowerCase() === path.toLowerCase());
      const open = (p) => p.toLowerCase() !== path.toLowerCase() && this.docs.has(p);
      const after = branch.slice(at + 1).find(open);
      const before = branch.slice(0, Math.max(at, 0)).reverse().find(open);
      branchNext = after || before || null;
    }

    // Order matters: drop the entry (and, if it was showing, activePath) before disposing
    // the model, so the content-change and validation handlers — both of which bail out
    // when there is no active file — can't fire against a model being torn down.
    if (wasActive) this.activePath = null;
    this.docs.delete(path);
    const i = this.mru.indexOf(path);
    if (i >= 0) this.mru.splice(i, 1);

    if (this.secondaryPath === path) {
      // The split pane was showing this file; move it to whatever is left, or fold it away.
      const fallback = this.openPaths()[0];
      if (fallback) this.showInSplit(fallback);
      else this.closeSplit();
    }
    if (wasActive) this.editor.setModel(null);
    doc.model.dispose();

    window.chrome.webview.hostObjects.host.NotifyFileClosed(path);

    if (wasActive) {
      clearTimeout(this._validateTimer);
      this.hideExternalChangeBanner();
      // Ưu tiên file còn lại trong cùng nhánh cây (branchNext); hết nhánh thì về file bạn đang xem trước đó (MRU), không phải tab nằm sát bên.
      const next = (branchNext && this.docs.has(branchNext)) ? branchNext : this.mru[this.mru.length - 1];
      if (next) this.activateDoc(next);
      else {
        this.setValidationBanners([]);
        if (window.bcodeOutline) window.bcodeOutline.refresh();
      }
    }

    if (window.bcodeTabs) window.bcodeTabs.render();
    this.refreshSplitPicker();
    return true;
  }

  /// "Close all / close the others" from the tab context menu. Stops at the first tab the
  /// user cancels out of, leaving that one (and everything after it) open — carrying on
  /// would close files past the point where they said no.
  async closeOthers(keepPath) {
    for (const path of this.openPaths()) {
      if (path === keepPath) continue;
      if (!(await this.closeDoc(path))) return;
    }
  }

  async closeAll() {
    for (const path of this.openPaths()) {
      if (!(await this.closeDoc(path))) return;
    }
  }

  /// F5: Save (như Ctrl+S) rồi mở đúng menu của file đang mở bằng trình duyệt mặc định (không qua Bcode.App).
  async saveAndRunMenu() {
    if (!this.activePath) return;
    await this.saveActive();
    if (this.dirty) return; // lưu lỗi (saveActive đã báo) — không chạy bản cũ
    try {
      const msg = await window.bcodeHost.call('BeginRunMenu', this.activePath);
      this.showToast(msg);
    } catch (e) {
      this.showToast('Không mở được trình duyệt: ' + e);
    }
  }

  /// Thông báo nhỏ góc trên bên phải (ngay dưới thanh tab, dễ thấy hơn góc dưới), tự tắt sau ms mili-giây (mặc định 5 giây).
  showToast(text, ms = 5000) {
    if (!text) return;
    let el = document.getElementById('bcodeToast');
    if (!el) {
      el = document.createElement('div');
      el.id = 'bcodeToast';
      el.style.cssText = 'position:fixed;right:24px;top:44px;max-width:420px;padding:8px 12px;border-radius:4px;' +
        'background:var(--bc-panel,#252526);color:var(--bc-text,#ddd);border:1px solid var(--bc-accent,#e8912d);' +
        'font-size:13px;z-index:30000;box-shadow:0 4px 14px rgba(0,0,0,.4)';
      document.body.appendChild(el);
    }
    el.textContent = text;
    el.style.display = 'block';
    clearTimeout(this._toastTimer);
    this._toastTimer = setTimeout(() => { el.style.display = 'none'; }, ms);
  }

  async saveActive() {
    if (!this.activePath) return;
    // A save is no longer instantaneous from the page's point of view — it runs on a host
    // worker now (see hostcall.js), so Ctrl+S twice in a row, or Ctrl+S during a slow write
    // to a share, would otherwise start a second one. The host serialises writes to a path
    // regardless; this just stops the queue forming, and keeps the dirty flag and the
    // write-time bookkeeping below from being updated out of order by two runs at once.
    if (this._saving) { this.showToast('Đang lưu file...', 1500); return; }
    this._saving = true;
    try {
      // Version taken with the text being written: anything typed while the save is in
      // flight stays dirty afterwards.
      const savedVersion = this.currentModel.getAlternativeVersionId();
      await window.bcodeHost.call('BeginSaveWithHistory', this.activePath, this.currentModel.getValue());
      this.markClean(this.activePath, savedVersion);
      // Báo "đã lưu" NGAY — các bước dọn cache bên dưới không được làm người dùng phải chờ (trên share, dựng lại
      // chỉ mục include là đọc từng file include qua mạng).
      const savedPath = this.activePath;
      const savedText = this.currentModel.getValue();
      this.showToast(`Đã lưu file thành công: ${fileNameOf(savedPath)}`, 2500);
      this.dismissedWriteTimeUtc = null;
      this.hideExternalChangeBanner();

      // The file just written may itself be one of the included entity files whose text
      // F12/hover resolution has cached. Chỉ khi đúng vậy mới bỏ cache; luôn làm nền, không await.
      if (window.bcodeEntity) {
        if (window.bcodeEntity.isCachedFile(savedPath)) window.bcodeEntity.invalidate();
        window.bcodeEntity.refreshIncludeIndex(savedPath, savedText); // không await
      }
      // Our own write just changed the file's mtime — record it as "loaded" so the next
      // poll doesn't mistake this save for an external change and nag about reloading it.
      // Poll 4 giây có thể chạy trước khi dòng này xong; chấp nhận được (tối đa 1 lần nhắc nhầm) nên không giữ _saving chờ nó.
      this._syncingWriteTime = true; // checkExternalChange tạm bỏ qua cho tới khi mốc ghi mới được ghi nhận
      window.bcodeHost.call('BeginGetFileWriteTimeUtc', savedPath)
        .then((t) => { if (this.activePath === savedPath) this.loadedWriteTimeUtc = t; })
        .catch(() => { /* best-effort — a stale loadedWriteTimeUtc just means one extra poll cycle */ })
        .finally(() => { this._syncingWriteTime = false; });
    } catch (e) {
      alert('Không ghi được file:\n' + this.activePath + '\n' + e);
    } finally {
      this._saving = false;
    }
  }

  /// Polled every few seconds (see constructor) while a file is open: compares the file's
  /// current on-disk write time against the one this.currentModel was actually loaded/saved
  /// from. A mismatch means someone else (or another Bcode/BcodeViewer instance) saved this
  /// file since — shows a banner offering to reload it, same idea as VS Code's "file changed
  /// on disk" prompt. Silently does nothing if the file is missing/locked/unreadable right
  /// now (GetFileWriteTimeUtc returns "" for that) — this is a periodic best-effort check,
  /// not something that should interrupt the user with a transient I/O hiccup.
  async checkExternalChange() {
    if (!this.activePath) return;
    // The interval that drives this doesn't wait for the previous check to come back. On a
    // share that has gone away each one can sit for seconds, so without this the timer
    // would stack up a fresh probe every 4s against a path already known to be unresponsive.
    if (this._checkingExternal || this._syncingWriteTime) return;
    this._checkingExternal = true;
    let current;
    try { current = await window.bcodeHost.call('BeginGetFileWriteTimeUtc', this.activePath); }
    catch { return; }
    finally { this._checkingExternal = false; }
    if (!current) return;
    if (current === this.loadedWriteTimeUtc) return; // unchanged since we loaded/saved it
    if (current === this.dismissedWriteTimeUtc) return; // user already said "not now" for this exact version
    this.showExternalChangeBanner(current);
  }

  showExternalChangeBanner(diskWriteTimeUtc) {
    const container = document.getElementById('externalChangeBanner');
    if (!container) return;
    container.innerHTML = '';
    container.style.display = 'flex';

    const text = document.createElement('span');
    text.className = 'msg';
    text.textContent = this.dirty
      ? 'File này đã được thay đổi từ máy khác. Tải lại sẽ mất thay đổi bạn chưa lưu ở đây — tải lại bản mới nhất?'
      : 'File này đã được thay đổi từ máy khác. Tải lại bản mới nhất?';
    text.title = this.activePath || '';

    // "Xem khác biệt" before "Reload", because deciding between the two versions is the
    // step that comes first — Reload on its own discards your edits without ever showing
    // what theirs actually changed, which is how work gets lost here.
    const diffBtn = document.createElement('button');
    diffBtn.className = 'reloadBtn secondary';
    diffBtn.textContent = 'Xem khác biệt';
    diffBtn.title = 'So sánh bản trên đĩa với bản đang mở';
    diffBtn.onclick = () => window.bcodeHistory.showExternalDiff(this, diskWriteTimeUtc);

    const reloadBtn = document.createElement('button');
    reloadBtn.className = 'reloadBtn';
    reloadBtn.textContent = 'Reload';
    reloadBtn.onclick = () => this.reloadFromDisk(diskWriteTimeUtc);

    const dismiss = document.createElement('span');
    dismiss.className = 'dismiss';
    dismiss.textContent = '✕';
    dismiss.title = 'Bỏ qua lần này — sẽ báo lại nếu có thay đổi mới hơn nữa';
    dismiss.onclick = () => {
      this.dismissedWriteTimeUtc = diskWriteTimeUtc;
      this.hideExternalChangeBanner();
    };

    container.appendChild(text);
    container.appendChild(diffBtn);
    container.appendChild(reloadBtn);
    container.appendChild(dismiss);
  }

  hideExternalChangeBanner() {
    const container = document.getElementById('externalChangeBanner');
    if (container) { container.style.display = 'none'; container.innerHTML = ''; }
  }

  /// Reloads the active file's content from disk in place, discarding any local unsaved
  /// changes — the "Reload" action on the external-change banner. Not routed through
  /// openFile(path) because that treats "same path already active" as a no-op (the normal
  /// case for double-clicking the same file twice), which is exactly the case this needs to
  /// actually do something.
  async reloadFromDisk(diskWriteTimeUtc) {
    if (!this.activePath) return;
    const path = this.activePath;
    let content;
    try {
      content = await window.bcodeHost.call('BeginReadFile', path);
    } catch (e) {
      alert('Không đọc được file:\n' + path + '\n' + e);
      return;
    }

    // setValue rather than a fresh model: the split pane may be showing this same model,
    // and replacing it here would leave that side bound to a disposed one.
    this.currentModel.setValue(content);
    this.markClean(path);
    this.bookmarks = new Set();
    this.renderBookmarks();
    this.loadedWriteTimeUtc = diskWriteTimeUtc;
    this.dismissedWriteTimeUtc = null;
    this.hideExternalChangeBanner();
    this.validateActive();
  }

  /// "Save As": the file picker itself has to be native (WinForms SaveFileDialog, shown by
  /// MainForm), so this just asks the host for a path and, once chosen, writes there and
  /// switches to editing that new path — same as most editors' Save As behavior.
  async saveActiveAs() {
    if (!this.activePath) return;
    const newPath = await window.bcodeHost.call('BeginChooseSaveAsPath', this.activePath);
    if (!newPath) return; // user cancelled
    try {
      await window.bcodeHost.call('BeginSaveWithHistory', newPath, this.currentModel.getValue());
    } catch (e) {
      alert('Không ghi được file:\n' + newPath + '\n' + e);
      return;
    }
    // The old tab has just been written elsewhere; leaving it open would show the same
    // content under two paths, with only one of them matching what Ctrl+S now writes.
    // Forced, because its buffer is identical to what was just saved — there is nothing
    // left to lose and nothing worth asking about.
    const previous = this.activePath;
    if (previous !== newPath) await this.closeDoc(previous, true);
    await this.openFile(newPath);
    this.showToast(`Đã lưu file thành công: ${fileNameOf(newPath)}`, 2500);
  }

  undo() { this.editor.trigger('toolbar', 'undo'); }
  redo() { this.editor.trigger('toolbar', 'redo'); }

  /// Used by the Hint Code panel's "Insert" action — pastes a saved snippet's code at the
  /// current selection/cursor, same as picking a snippet from FCodeViewer's own Hint Code
  /// list. Replaces the selection if there is one, otherwise inserts at the caret.
  insertTextAtCursor(text) {
    // With no document open the editor has no model, so getSelection() is null and
    // executeEdits throws. Closing a file is now a real state (see closeActive), so every
    // caret-based action has to tolerate it instead of assuming a file is always loaded.
    if (!this.activePath) return;
    const sel = this.editor.getSelection();
    if (!sel) return;
    this.editor.executeEdits('hint-insert', [{ range: sel, text }]);
    this.editor.focus();
  }

  /// Monaco's own built-in "Toggle Line Comment" action — works out of the box per
  /// language (XML's comment rules give <!-- --> block comments; there's no dedicated
  /// FastBusiness "--" toggle here, unlike FCode's own, but it's the same underlying idea).
  toggleComment() {
    this.editor.getAction('editor.action.commentLine')?.run();
  }

  toggleBookmark() {
    const pos = this.editor.getPosition();
    if (!pos) return; // no document open
    const line = pos.lineNumber;
    if (this.bookmarks.has(line)) this.bookmarks.delete(line);
    else this.bookmarks.add(line);
    this.renderBookmarks();
  }

  /// Cycles forward through bookmarked lines from the caret's current line, wrapping back
  /// to the first bookmark past the end — matches a typical "next bookmark" toolbar button.
  nextBookmark() {
    if (this.bookmarks.size === 0) return;
    const curPos = this.editor.getPosition();
    if (!curPos) return;
    const cur = curPos.lineNumber;
    const sorted = [...this.bookmarks].sort((a, b) => a - b);
    const next = sorted.find((l) => l > cur) ?? sorted[0];
    this.editor.revealLineInCenter(next);
    this.editor.setPosition({ lineNumber: next, column: 1 });
    this.editor.focus();
  }

  renderBookmarks() {
    const decorations = [...this.bookmarks].map((line) => ({
      range: new monaco.Range(line, 1, line, 1),
      options: { isWholeLine: false, glyphMarginClassName: 'bookmarkGlyph' }
    }));
    this.bookmarkDecorations = this.editor.deltaDecorations(this.bookmarkDecorations, decorations);
  }

  // F12: same convention already proven in ScriptEditorControl.cs on the Bcode.App side —
  // <!ENTITY Name SYSTEM "relative/path"> declared in THIS file, resolved relative to this
  // file's own folder, and opened in place (see openFile) — it becomes the current file,
  // added to the left "recent files" tree so getting back to what you came from is just a
  // click there.
  async jumpToEntityAtCaret(mode = 'peek') {
    if (!this.activePath) return;
    // Resolution lives in entity.js: what `&Name;` means depends on the whole chain of
    // files the document includes, not just on this one's own DOCTYPE. A SYSTEM entity
    // opens its file; a value entity — most of them, and where most of an FCode
    // controller's real SQL and JavaScript lives — opens in the peek window, because its
    // "definition" is the text itself rather than somewhere to go.
    // Nothing happens when the word under the caret names no entity anywhere in that
    // chain, or names a file that isn't on disk — same as before, and the missing file is
    // already reported by the Problems panel rather than by a silent F12.
    if (window.bcodeEntity) await window.bcodeEntity.goToOrPeek(mode);
  }

  /// Jumps to the first occurrence of a tag, used for "Goto Command"/"Goto Response Tag" —
  /// these are fixed section jumps (the menu is the same regardless of cursor position in
  /// FCode's own UI), not per-field lookups.
  gotoTag(tagRegex) {
    if (!this.activePath) return;
    const model = this.editor.getModel();
    const text = model.getValue();
    const m = tagRegex.exec(text);
    tagRegex.lastIndex = 0;
    if (!m) return;
    const pos = offsetToPosition(text, m.index);
    this.editor.revealLineInCenter(pos.line);
    this.editor.setPosition({ lineNumber: pos.line, column: pos.col });
    this.editor.focus();
  }

  gotoCommand() { this.gotoTag(/<command\b/); }
  gotoResponseTag() { this.gotoTag(/<response\b/); }

  /// The handler name referenced on the current line, e.g. onchange="onChange$Voucher$X(this)"
  /// -> "onChange$Voucher$X" — used by both Goto Function and Create Function.
  handlerNameAtCaret() {
    const model = this.editor.getModel();
    const pos = this.editor.getPosition();
    if (!model || !pos) return null;
    const lineText = model.getLineContent(pos.lineNumber);
    const m = /=["']?([A-Za-z_$][\w$]*)\s*\(/.exec(lineText);
    return m ? m[1] : null;
  }

  /// F11: jumps to "function Name(" for the handler referenced on the current line (e.g.
  /// a field's onchange="Name(this)"). Falls back to the <script> section if the current
  /// line doesn't reference one, so F11 always does *something* useful.
  gotoFunctionAtCaret() {
    if (!this.activePath) return;
    const name = this.handlerNameAtCaret();
    const model = this.editor.getModel();
    const text = model.getValue();
    if (name) {
      const idx = text.indexOf('function ' + name + '(');
      if (idx >= 0) {
        const pos = offsetToPosition(text, idx);
        this.editor.revealLineInCenter(pos.line);
        this.editor.setPosition({ lineNumber: pos.line, column: pos.col });
        this.editor.focus();
        return;
      }
    }
    this.gotoTag(/<script\b/);
  }

  /// "Create Function": if the handler referenced on the current line has no matching
  /// "function Name(" yet, inserts a skeleton right before the <script> block's closing
  /// "]]>" and jumps the caret into it — same idea as an IDE's "generate method stub".
  createFunctionAtCaret() {
    if (!this.activePath) return;
    const name = this.handlerNameAtCaret();
    if (!name) { alert('Không tìm thấy tên hàm ở dòng hiện tại (cần dạng onchange="Name(this)").'); return; }

    const model = this.editor.getModel();
    const text = model.getValue();
    if (text.indexOf('function ' + name + '(') >= 0) { this.gotoFunctionAtCaret(); return; }

    const scriptStart = text.indexOf('<script');
    if (scriptStart < 0) { alert('Không tìm thấy <script> trong file này.'); return; }
    const closeIdx = text.indexOf(']]>', scriptStart);
    if (closeIdx < 0) { alert('Không tìm thấy điểm chèn (]]>) trong <script>.'); return; }

    const stub = `\nfunction ${name}(o) {\n\t\n}\n`;
    const insertPos = offsetToPosition(text, closeIdx);
    model.pushEditOperations([], [{
      range: new monaco.Range(insertPos.line, 1, insertPos.line, 1),
      text: stub
    }], () => null);

    // Re-locate the stub's body line precisely (line 2 of the inserted block) to place the
    // caret ready to type, rather than just leaving it at the insertion point.
    const newText = model.getValue();
    const stubIdx = newText.indexOf(`function ${name}(o) {`);
    const bodyLineOffset = stubIdx + `function ${name}(o) {\n\t`.length;
    const bodyPos = offsetToPosition(newText, bodyLineOffset);
    this.editor.revealLineInCenter(bodyPos.line);
    this.editor.setPosition({ lineNumber: bodyPos.line, column: bodyPos.col });
    this.editor.focus();
  }

  /// The f.request('X', ...) on the caret's line (or the nearest one above it within 3 lines).
  requestAtCaret() {
    const model = this.editor.getModel();
    const pos = this.editor.getPosition();
    if (!model || !pos || typeof fcFindRequests !== 'function') return null;
    const text = model.getValue();
    let best = null;
    for (const r of fcFindRequests(text)) {
      const line = offsetToPosition(text, r.index).line;
      if (line <= pos.lineNumber && pos.lineNumber - line <= 3 && (!best || line >= best.line)) best = { ...r, line };
    }
    return best;
  }

  /// "Tạo <action> cho f.request": the server-side half of a request — <action id="X"> with a CDATA
  /// stub listing what the JS sends, inserted before </response> (a <response> is created before
  /// the root's closing tag when there is none). An action that already exists, here or in an
  /// included entity, is revealed instead of duplicated.
  async createActionAtCaret() {
    if (!this.activePath) return;
    const req = this.requestAtCaret();
    if (!req) { alert("Không thấy f.request('Ten', ...) ở dòng hiện tại."); return; }
    const model = this.editor.getModel();
    const text = model.getValue();
    const id = req.actionId;

    const own = new RegExp(`<action\\s+id\\s*=\\s*"${id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}"`, 'i').exec(text);
    if (own) {
      const p = offsetToPosition(text, own.index);
      this.revealPosition(p.line, p.col);
      this.showToast(`<action id="${id}"> đã có trong file này.`, 3000);
      return;
    }
    const responses = (text.match(/<response\b[^>]*>[\s\S]*?<\/response>/gi) || []).join('\n');
    if (window.bcodeEntity && /&[A-Za-z_][\w.:$-]*;/.test(responses)) {
      try {
        const expanded = (await window.bcodeEntity.expand(responses)).text;
        if (fcActions(expanded).has(id.toLowerCase())) {
          this.showToast(`<action id="${id}"> đã có trong một ENTITY được include ở <response> — không tạo thêm.`, 5000);
          return;
        }
      } catch { /* can't read the share — create it anyway */ }
    }

    const sent = req.resolved && req.fields.length
      ? req.fields.map((f) => '@' + f.name + (/^infinite$/i.test(f.type || '') ? ' (bảng)' : '')).join(', ')
      : '(không đọc được danh sách field của request — tự điền)';

    const closeResponse = text.lastIndexOf('</response>');
    let insertAt, indent, wrap;
    if (closeResponse >= 0) {
      const lineStart = text.lastIndexOf('\n', closeResponse) + 1;
      const base = text.slice(lineStart, closeResponse).match(/^\s*/)[0];
      indent = base + '    ';
      insertAt = lineStart;
      wrap = false;
    } else {
      const rootClose = text.search(/<\/[\w:.-]+>\s*$/);
      if (rootClose < 0) { alert('Không tìm thấy </response> hay thẻ đóng gốc để chèn <action>.'); return; }
      insertAt = text.lastIndexOf('\n', rootClose) + 1;
      indent = '        ';
      wrap = true;
    }

    const body = [
      `${indent}<action id="${id}">`,
      `${indent}    <text>`,
      `${indent}        <![CDATA[`,
      `-- f.request('${id}') gửi: ${sent}`,
      'select 1 as val',
      ']]>',
      `${indent}    </text>`,
      `${indent}</action>`,
    ];
    const block = (wrap
      ? ['', '    <response>', ...body, '    </response>']
      : ['', ...body]).join('\n') + '\n';

    const p = offsetToPosition(text, insertAt);
    this.editor.pushUndoStop();
    this.editor.executeEdits('create-action', [{ range: new monaco.Range(p.line, 1, p.line, 1), text: block }]);
    this.editor.pushUndoStop();

    const caret = offsetToPosition(model.getValue(), insertAt + block.indexOf('select 1 as val'));
    this.editor.setSelection(new monaco.Selection(caret.line, 1, caret.line, 'select 1 as val'.length + 1));
    this.editor.revealLineInCenter(caret.line);
    this.editor.focus();
  }

  /// The "App_Data" folder above the currently open file — found by locating that segment
  /// in the path rather than assuming a fixed depth (Dir/Grid/Filter files sit at different
  /// depths under Controllers). Returns null (and alerts) if the open file isn't under one.
  appDataRoot(silent) {
    if (!this.activePath) return null;
    const idx = this.activePath.toLowerCase().indexOf('\\app_data\\');
    if (idx < 0) {
      if (!silent) alert('Không xác định được App_Data trong đường dẫn file này.');
      return null;
    }
    return this.activePath.substring(0, idx + '\\App_Data'.length);
  }

  /// "Open Folder > Images/Options/Lookup/Templates" — well-known siblings of Controllers
  /// under App_Data in every FastBusiness site.
  openAppDataFolder(subfolder) {
    const root = this.appDataRoot();
    if (!root) return;
    window.chrome.webview.hostObjects.host.OpenFolder(root + '\\' + subfolder);
  }

  /// "Open File Config" — the fixed shared config files FCode's own menu links to,
  /// resolved from the paths its own internal gen_update search config actually uses
  /// (App_Data\Controllers\Options\Message.xml etc.) rather than guessed.
  openConfigFile(relativePath) {
    const root = this.appDataRoot();
    if (!root) return;
    this.openFile(root + '\\' + relativePath);
  }

  /// "Goto File" của <items style="..." controller="X">: file của controller X nằm ở
  /// App_Data\Controllers\<thư mục>\X.f|.xml — style Lookup/AutoComplete ở Lookup, Grid ở Grid
  /// (đối chiếu trên site thật). Không thấy ở thư mục đúng style thì dò các thư mục controller
  /// khác. Trả về các file có thật, .f đứng trước .xml; một lần gọi host cho mọi ứng viên.
  async resolveControllerFiles(controller, style) {
    const root = this.appDataRoot(true);
    if (!root || !controller) return [];
    const byStyle = { grid: ['Grid'], lookup: ['Lookup'], autocomplete: ['Lookup'] };
    const preferred = byStyle[String(style || '').toLowerCase()] || [];
    const others = ['Dir', 'Grid', 'Lookup', 'Filter', 'List', 'Report', 'View', 'Query'].filter((d) => !preferred.includes(d));
    const toCandidates = (dirs) => dirs.flatMap((d) => ['.f', '.xml'].map((ext) => ({
      rel: d + '\\' + controller + ext,
      path: root + '\\Controllers\\' + d + '\\' + controller + ext
    })));
    const first = toCandidates(preferred);
    const all = first.concat(toCandidates(others));
    let exists;
    try { exists = JSON.parse(await window.bcodeHost.call('BeginPathsExist', JSON.stringify(all.map((c) => c.path)))); }
    catch { return []; }
    const found = all.filter((_, i) => exists[i]);
    const inPreferred = found.filter((c) => first.includes(c));
    return inPreferred.length ? inPreferred : found;
  }

  /// "Clone file...": nhân bản file thành tên gốc mới ngay trong thư mục của nó (vctck2.xml → vctck3.xml), hỏi có nhân
  /// bản luôn các file cùng tên gốc trong thư mục đó không (vd Item.f + Item.xml), không ghi đè file có sẵn — giống
  /// "Clone files" của File Lookup bên Bcode. Chỉ đổi TÊN file, nội dung giữ nguyên. Mở bản mới (trừ khi opts.open === false);
  /// trả { newBase, created } hoặc null nếu huỷ / lỗi.
  async cloneFile(sourcePath, opts = {}) {
    if (!sourcePath) return null;
    const cut = sourcePath.lastIndexOf('\\');
    const dir = sourcePath.substring(0, cut);
    const baseOf = (name) => (name.lastIndexOf('.') > 0 ? name.substring(0, name.lastIndexOf('.')) : name);
    const oldBase = baseOf(sourcePath.substring(cut + 1));
    const newBase = ((await window.bcodeUi.prompt(`Tên file mới của "${oldBase}":`, oldBase + '2', { title: 'Clone file' })) || '').trim();
    if (!newBase || newBase.toLowerCase() === oldBase.toLowerCase()) return null;

    let sources = [sourcePath];
    try {
      const entries = JSON.parse(await window.bcodeHost.call('BeginListDirectory', dir));
      if (!entries.error) {
        const siblings = entries.filter((e) => !e.isDirectory
          && e.path.toLowerCase() !== sourcePath.toLowerCase()
          && baseOf(e.name).toLowerCase() === oldBase.toLowerCase());
        if (siblings.length && await window.bcodeUi.confirm(`Cùng thư mục còn ${siblings.length} file cùng tên gốc:\n${siblings.map((s) => s.name).join('\n')}\n\nNhân bản luôn cả những file này?`))
          sources = sources.concat(siblings.map((s) => s.path));
      }
    } catch { /* không liệt kê được thư mục thì chỉ nhân bản đúng file này */ }

    let result;
    try { result = JSON.parse(await window.bcodeHost.call('BeginCloneFiles', JSON.stringify(sources), newBase)); }
    catch (e) { alert('Không nhân bản được:\n' + e); return null; }
    if (result.error) { alert('Không nhân bản được:\n' + result.error); return null; }
    if (opts.open !== false) await this.openFile(result.created[0]);
    return { newBase, created: result.created };
  }

  /// "List Extend"/"Voucher Extend" submenus: FCode's own config confirms files named
  /// Voucher.Controller.001, .002, ... live in Controllers\Grid\Config\Include — rather
  /// than guess how many exist (the real menu showed up to "List 109"), this lists
  /// whatever's actually there matching the given prefix.
  async listIncludeConfigFiles(prefix) {
    const root = this.appDataRoot(true);
    if (!root) return [];
    const dir = root + '\\Controllers\\Grid\\Config\\Include';
    try {
      const json = await window.bcodeHost.call('BeginListDirectory', dir);
      const entries = JSON.parse(json);
      if (entries.error) return [];
      return entries.filter((e) => !e.isDirectory && e.name.toLowerCase().startsWith(prefix.toLowerCase()));
    } catch {
      return [];
    }
  }

  async refreshActive() {
    if (!this.activePath) return;
    if (this.dirty && !(await window.bcodeUi.confirm('File có thay đổi chưa lưu. Tải lại từ đĩa và bỏ thay đổi?', { danger: true }))) return;
    try {
      const content = await window.bcodeHost.call('BeginReadFile', this.activePath);
      const viewState = this.editor.saveViewState();
      this.currentModel.setValue(content);
      this.markClean(this.activePath);
      this.editor.restoreViewState(viewState);
    } catch (e) {
      alert('Không đọc lại được file:\n' + e);
    }
  }

  getActiveContent() {
    return this.currentModel ? this.currentModel.getValue() : null;
  }

  /// Called from MainForm after the Hint Code dialog closes (and after Settings changes the
  /// shared template folder) so a snippet saved a moment ago is suggestable immediately.
  /// Routed through here rather than called on window.bcodeCompletion directly because the
  /// host's ExecJsAsync helper only ever addresses window.bcodeViewer.
  reloadSnippets() {
    if (window.bcodeCompletion) window.bcodeCompletion.reloadSnippets();
  }

  /// Called from MainForm when the theme changes, so the page re-skins in place rather
  /// than needing a reload. Routed through here for the same reason as reloadSnippets:
  /// the host's ExecJsAsync helper only ever addresses window.bcodeViewer.
  /// MainForm.PushShellTree — cây file gần đây (đã dựng bên C#) để shell.js vẽ bên trái.
  shellTree(data) {
    if (window.bcodeShell) window.bcodeShell.setTree(data);
    else this._pendingShellTree = data;
  }

  /// MainForm.Msg — hộp thông báo/xác nhận thay MessageBox; báo kết quả về host bằng ResolveUiDialog.
  async uiDialog(p) {
    let result = '';
    try {
      const opts = { title: p.title, kind: p.kind, yesText: p.yesText, noText: p.noText, danger: p.danger };
      result = p.type === 'yesnocancel' ? await window.bcodeUi.yesNoCancel(p.message, opts)
        : p.type === 'confirm' ? String(await window.bcodeUi.confirm(p.message, opts))
        : (await window.bcodeUi.alert(p.message, opts), '');
    } finally {
      try { window.chrome.webview.hostObjects.host.ResolveUiDialog(p.id, result); } catch { /* host gone */ }
    }
  }

  /// File > Settings... từ MainForm — hộp thoại vẽ trong trang (settings.js) để theo theme đang chọn.
  openSettings() {
    if (window.bcodeSettings) window.bcodeSettings.show();
  }

  /// Nút "Hint" / lưu gợi ý AI thành Hint Code từ MainForm — hộp thoại trong trang (templates.js).
  openHintCode(initialCode) {
    if (window.bcodeHintCode) window.bcodeHintCode.show(initialCode);
  }

  /// File > New from Template (Ctrl+N) từ MainForm — hộp thoại trong trang (templates.js).
  openNewFromTemplate() {
    if (window.bcodeTemplatePicker) window.bcodeTemplatePicker.show();
  }

  reloadTheme() {
    if (window.bcodeTheme) window.bcodeTheme.init();
  }

  /// Entry point for the toolbar/menu on the WinForms side (MainForm's ExecJsAsync only
  /// ever addresses window.bcodeViewer), matching Ctrl+Shift+H in the editor.
  openHistory() {
    window.bcodeHistory.showHistory(this);
  }

  // Entry points for the WinForms toolbar/menu. MainForm's ExecJsAsync only ever addresses
  // window.bcodeViewer, so anything the native chrome can click has to hang off this class
  // even when the work belongs to a panel.
  openSearch() {
    const sel = this.editor.getSelection();
    const seed = sel && this.currentModel ? this.currentModel.getValueInRange(sel) : '';
    window.bcodeSearch.open(seed);
  }

  toggleProblemsPanel() { window.bcodeProblems.toggle(); }
  runSql() { window.bcodeSqlRun.run(); }
  toggleOutlinePanel() { window.bcodeOutline.toggle(); }
  toggleDirPreview() { if (window.bcodeDirPreview) window.bcodeDirPreview.toggle(); }
  findReferences() { window.bcodeOutline.findReferencesAtCaret(); }

  /// Ctrl+Space equivalent for the toolbar/context menu — Monaco's own trigger action.
  triggerSuggest() {
    this.editor.getAction('editor.action.triggerSuggest')?.run();
  }
}

/// Last path segment of a Windows or UNC path — tab captions, result groups, prompts.
function fileNameOf(path) {
  if (!path) return '';
  const i = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
  return i >= 0 ? path.slice(i + 1) : path;
}

/// Everything before that segment.
function dirNameOf(path) {
  if (!path) return '';
  const i = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
  return i >= 0 ? path.slice(0, i) : '';
}

function resolvePath(baseDir, relative) {
  const parts = (baseDir + '\\' + relative).split(/[\\/]+/);
  const stack = [];
  for (const part of parts) {
    if (part === '' || part === '.') continue;
    if (part === '..') stack.pop();
    else stack.push(part);
  }
  // UNC paths start with \\server\... — the split above eats the leading empties, so
  // restore the leading "\\" when the original path had one.
  const prefix = baseDir.startsWith('\\\\') ? '\\\\' : '';
  return prefix + stack.join('\\');
}

