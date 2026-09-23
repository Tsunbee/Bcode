// Monaco setup, tab model management, F12 entity-jump, Ctrl+G/Ctrl+Shift+O outline —
// all reimplemented here in JS rather than shared with Bcode.App's C# ScriptEditorControl:
// Monaco already has its own text engine, so porting the RTF-highlighter approach makes no
// sense, and file I/O goes through window.chrome.webview.hostObjects.host (see MainForm.cs's
// EditorBridge) instead of a direct filesystem call, since page JS can't touch arbitrary
// paths on its own.

const ENTITY_DECL_RE = /<!ENTITY\s+%?\s*([A-Za-z0-9_]+)\s+SYSTEM\s+"([^"]+)"/g;
const WORD_RE = /[A-Za-z0-9_]/;

function detectLanguage(path) {
  const ext = path.slice(path.lastIndexOf('.')).toLowerCase();
  switch (ext) {
    // 'fcode-xml' rather than plain 'xml' — same markup colouring plus real JavaScript,
    // T-SQL and CSS inside the CDATA blocks that hold them (see fcode-language.js).
    // Falls back to 'xml' if that language failed to register, so a tokenizer problem
    // costs colour and nothing else.
    case '.xml': case '.f': case '.ent':
      return window.bcodeFcodeLanguageReady ? window.FCODE_LANGUAGE_ID : 'xml';
    case '.sql': return 'sql';
    case '.js': return 'javascript';
    case '.aspx': case '.html': return 'html';
    case '.json': return 'json';
    default: return 'plaintext';
  }
}

// Same structural categories as ScriptEditorControl's Ctrl+G dialog on the Bcode.App side —
// fields/views/command/script/response/ENTITY — but here it feeds Monaco's own outline
// (Ctrl+Shift+O) instead of a custom dialog.
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
    // Several documents at a time, one tab each (see tabs.js). This used to be a strictly
    // single-document editor, matching FCodeViewer's own UI: the WinForms tree on the left
    // was the only file switcher and opening anything replaced what was on screen. That
    // works until the work is "this <field> and the function it calls", which is two files
    // — and going back through the tree drops the caret position, the fold state and the
    // undo stack every time. Each entry here keeps all three.
    //
    // Per-document state lives in this map, NOT on `this`: the accessors below
    // (currentModel/dirty/bookmarks/...) forward to whichever document is active, so every
    // method written against the single-document version keeps working unchanged.
    //   path -> { model, dirty, bookmarks:Set, bookmarkDecorations:[], viewState,
    //             loadedWriteTimeUtc, dismissedWriteTimeUtc }
    this.docs = new Map(); // insertion order == tab order
    this.activePath = null;
    /// Most-recently-used order, newest last — what Ctrl+Tab cycles and what closing a tab
    /// falls back to. Tab order would jump to a neighbour you were never looking at.
    this.mru = [];
    this._validateTimer = null;

    // Split view (toggleSplit): a second editor on the right, created lazily the first time
    // it's asked for — a second Monaco instance is not free, and most sessions never split.
    this.editorSecondary = null;
    this.secondaryPath = null;

    // "File changed on another machine" watch — see checkExternalChange/openFile/saveActive.
    // loadedWriteTimeUtc is the write time a document was actually loaded/saved from;
    // dismissedWriteTimeUtc is set when the user closes the banner for one specific on-disk
    // version, so the same change doesn't keep nagging every poll (a genuinely newer save
    // still will). Both are per-document now — a background tab can go stale too, and it
    // gets its banner when you come back to it.
    setInterval(() => this.checkExternalChange(), 4000);

    this.editor = monaco.editor.create(document.getElementById(containerId), {
      // Not a literal 'vs-dark' any more — the theme is whatever the host's active one
      // defines (see theme.js, which has already run by this point; index.html awaits it).
      theme: window.bcodeTheme ? window.bcodeTheme.monacoThemeName : 'vs-dark',
      automaticLayout: true,
      fontFamily: "'Roboto', Consolas, monospace",
      fontSize: 15,
      minimap: { enabled: true },
      mouseWheelZoom: true, // Ctrl + lăn chuột để phóng to/thu nhỏ chữ nhanh
      glyphMargin: true // needed for the Bookmark gutter dot — see toggleBookmark
    });

    // Roboto nạp qua @font-face (Web/fonts/roboto.css) — Monaco đo bề rộng ký tự ngay lúc
    // tạo editor, nếu font chưa tải xong thì con trỏ/vùng chọn sẽ lệch so với chữ. Ép tải
    // font rồi bảo Monaco đo lại.
    if (document.fonts && document.fonts.load) {
      document.fonts.load("15px 'Roboto'").catch(() => {}).then(() => monaco.editor.remeasureFonts());
    }

    // Backs MainForm's status bar ("Ln X, Col Y") — same idea as FCodeViewer's own.
    this.editor.onDidChangeCursorPosition((e) => {
      window.chrome.webview.hostObjects.host.NotifyCursorChanged(e.position.lineNumber, e.position.column);
    });

    monaco.languages.registerDocumentSymbolProvider(['xml', 'fcode-xml'], {
      provideDocumentSymbols: (model) => buildSymbols(model.getValue())
    });

    this.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => this.saveActive());
    this.editor.addCommand(monaco.KeyCode.F12, () => this.jumpToEntityAtCaret());
    this.editor.addCommand(monaco.KeyCode.F11, () => this.gotoFunctionAtCaret());
    // Ctrl+I — lightweight version of VSCode Copilot's inline generate (see
    // BcodeDialogs.showInlineGenerate in contextmenu.js): asks Claude for code based on
    // a short instruction, shown for review before an explicit "Insert" applies it.
    this.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyI, () => window.bcodeDialogs.showInlineGenerate(this));

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
      run: (ed) => ed.getAction('editor.action.copyLinesDownAction')?.run(),
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
      if (!this.dirty) {
        this.dirty = true;
        window.chrome.webview.hostObjects.host.NotifyDirtyChanged(this.activePath, true);
      }
      // Re-run the same checks FCodeViewer itself runs (missing ENTITY file, duplicate
      // field names) as the user types, not just on open — debounced so a fast typist
      // doesn't trigger a PathExists round-trip per keystroke.
      clearTimeout(this._validateTimer);
      this._validateTimer = setTimeout(() => this.validateActive(), 400);
      // The tab's dirty marker and the outline both follow the text, on the same debounce
      // budget — rebuilding a symbol tree per keystroke is the one thing here big enough
      // to be felt on a 4000-line controller.
      if (window.bcodeTabs) window.bcodeTabs.render();
      clearTimeout(this._outlineTimer);
      this._outlineTimer = setTimeout(() => window.bcodeOutline && window.bcodeOutline.refresh(), 400);
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
      fontFamily: "'Roboto', Consolas, monospace",
      fontSize: 15,
      minimap: { enabled: false }, // narrow by definition; the minimap costs more width than it earns here
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
      content = await window.chrome.webview.hostObjects.host.ReadFile(path);
    } catch (e) {
      alert('Không đọc được file:\n' + path + '\n' + e);
      return;
    }

    let writeTime = null;
    try { writeTime = await window.chrome.webview.hostObjects.host.GetFileWriteTimeUtc(path); }
    catch { /* unreadable mtime just means one extra poll before the watch settles */ }

    // Between the await above and here another open of the same path may have completed
    // (double-click, or a search result clicked twice). Creating a second model for one
    // file would leave two tabs editing the same bytes.
    if (this.docs.has(path)) { this.activateDoc(path); return; }

    this.docs.set(path, {
      model: monaco.editor.createModel(content, detectLanguage(path)),
      dirty: false,
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
  closeDoc(path, force) {
    const doc = this.docs.get(path);
    if (!doc) return true;
    if (!force && doc.dirty &&
        !confirm(`"${fileNameOf(path)}" có thay đổi chưa lưu. Đóng và bỏ thay đổi?`)) {
      return false;
    }

    const wasActive = this.activePath === path;

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
      // Back to the file you were on before this one, not to whichever tab happens to sit
      // next to it.
      const next = this.mru[this.mru.length - 1];
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
  closeOthers(keepPath) {
    for (const path of this.openPaths()) {
      if (path === keepPath) continue;
      if (!this.closeDoc(path)) return;
    }
  }

  closeAll() {
    for (const path of this.openPaths()) {
      if (!this.closeDoc(path)) return;
    }
  }

  async saveActive() {
    if (!this.activePath) return;
    try {
      await window.chrome.webview.hostObjects.host.SaveWithHistory(this.activePath, this.currentModel.getValue());
      this.dirty = false;
      // The file just written may itself be one of the included entity files whose text
      // F12/hover resolution has cached.
      if (window.bcodeEntity) {
        window.bcodeEntity.invalidate();
        window.bcodeEntity.refreshIncludeIndex(this.activePath, this.currentModel.getValue());
      }
      window.chrome.webview.hostObjects.host.NotifyDirtyChanged(this.activePath, false);
      // Our own write just changed the file's mtime — record it as "loaded" so the next
      // poll doesn't mistake this save for an external change and nag about reloading it.
      try { this.loadedWriteTimeUtc = await window.chrome.webview.hostObjects.host.GetFileWriteTimeUtc(this.activePath); }
      catch { /* best-effort — a stale loadedWriteTimeUtc just means one extra poll cycle */ }
      this.dismissedWriteTimeUtc = null;
      this.hideExternalChangeBanner();
    } catch (e) {
      alert('Không ghi được file:\n' + this.activePath + '\n' + e);
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
    let current;
    try { current = await window.chrome.webview.hostObjects.host.GetFileWriteTimeUtc(this.activePath); }
    catch { return; }
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
      content = await window.chrome.webview.hostObjects.host.ReadFile(path);
    } catch (e) {
      alert('Không đọc được file:\n' + path + '\n' + e);
      return;
    }

    // setValue rather than a fresh model: the split pane may be showing this same model,
    // and replacing it here would leave that side bound to a disposed one.
    this.currentModel.setValue(content);
    this.dirty = false;
    window.chrome.webview.hostObjects.host.NotifyDirtyChanged(path, false);
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
    const newPath = await window.chrome.webview.hostObjects.host.ChooseSaveAsPath(this.activePath);
    if (!newPath) return; // user cancelled
    try {
      await window.chrome.webview.hostObjects.host.SaveWithHistory(newPath, this.currentModel.getValue());
    } catch (e) {
      alert('Không ghi được file:\n' + newPath + '\n' + e);
      return;
    }
    // The old tab has just been written elsewhere; leaving it open would show the same
    // content under two paths, with only one of them matching what Ctrl+S now writes.
    // Forced, because its buffer is identical to what was just saved — there is nothing
    // left to lose and nothing worth asking about.
    const previous = this.activePath;
    if (previous !== newPath) this.closeDoc(previous, true);
    await this.openFile(newPath);
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
  async jumpToEntityAtCaret() {
    if (!this.activePath) return;
    // Resolution lives in entity.js: what `&Name;` means depends on the whole chain of
    // files the document includes, not just on this one's own DOCTYPE. A SYSTEM entity
    // opens its file; a value entity — most of them, and where most of an FCode
    // controller's real SQL and JavaScript lives — opens in the peek window, because its
    // "definition" is the text itself rather than somewhere to go.
    // Nothing happens when the word under the caret names no entity anywhere in that
    // chain, or names a file that isn't on disk — same as before, and the missing file is
    // already reported by the Problems panel rather than by a silent F12.
    if (window.bcodeEntity) await window.bcodeEntity.goToOrPeek();
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

  /// "List Extend"/"Voucher Extend" submenus: FCode's own config confirms files named
  /// Voucher.Controller.001, .002, ... live in Controllers\Grid\Config\Include — rather
  /// than guess how many exist (the real menu showed up to "List 109"), this lists
  /// whatever's actually there matching the given prefix.
  async listIncludeConfigFiles(prefix) {
    const root = this.appDataRoot(true);
    if (!root) return [];
    const dir = root + '\\Controllers\\Grid\\Config\\Include';
    try {
      const json = await window.chrome.webview.hostObjects.host.ListDirectory(dir);
      const entries = JSON.parse(json);
      if (entries.error) return [];
      return entries.filter((e) => !e.isDirectory && e.name.toLowerCase().startsWith(prefix.toLowerCase()));
    } catch {
      return [];
    }
  }

  async refreshActive() {
    if (!this.activePath) return;
    if (this.dirty && !confirm('File có thay đổi chưa lưu. Tải lại từ đĩa và bỏ thay đổi?')) return;
    try {
      const content = await window.chrome.webview.hostObjects.host.ReadFile(this.activePath);
      const viewState = this.editor.saveViewState();
      this.currentModel.setValue(content);
      this.dirty = false;
      window.chrome.webview.hostObjects.host.NotifyDirtyChanged(this.activePath, false);
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
