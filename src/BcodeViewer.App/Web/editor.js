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
    case '.xml': case '.f': case '.ent': return 'xml';
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
    // Single document at a time — matches FCodeViewer's own UI, which has no tab strip:
    // the left panel (a WinForms tree in MainForm.cs, grouped "Project (N)") is the file
    // switcher, and opening any file (F12, Open File Config, double-click in that tree)
    // replaces what's shown here instead of adding another tab.
    this.currentModel = null;
    this.activePath = null;
    this.dirty = false;
    this.bookmarks = new Set(); // line numbers; reset on every openFile — see there
    this.bookmarkDecorations = [];
    this._validateTimer = null;

    this.editor = monaco.editor.create(document.getElementById(containerId), {
      theme: 'vs-dark',
      automaticLayout: true,
      fontFamily: 'Consolas',
      fontSize: 13,
      minimap: { enabled: true },
      glyphMargin: true // needed for the Bookmark gutter dot — see toggleBookmark
    });

    // Backs MainForm's status bar ("Ln X, Col Y") — same idea as FCodeViewer's own.
    this.editor.onDidChangeCursorPosition((e) => {
      window.chrome.webview.hostObjects.host.NotifyCursorChanged(e.position.lineNumber, e.position.column);
    });

    monaco.languages.registerDocumentSymbolProvider('xml', {
      provideDocumentSymbols: (model) => buildSymbols(model.getValue())
    });

    this.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => this.saveActive());
    this.editor.addCommand(monaco.KeyCode.F12, () => this.jumpToEntityAtCaret());
    this.editor.addCommand(monaco.KeyCode.F11, () => this.gotoFunctionAtCaret());
    // Ctrl+I — lightweight version of VSCode Copilot's inline generate (see
    // BcodeDialogs.showInlineGenerate in contextmenu.js): asks Claude for code based on
    // a short instruction, shown for review before an explicit "Insert" applies it.
    this.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyI, () => window.bcodeDialogs.showInlineGenerate(this));

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
    });
  }

  /// Mirrors two of FCodeViewer's own red validation banners (see screenshots this was
  /// built from): a `<!ENTITY Name SYSTEM "path">` whose target file doesn't exist on disk,
  /// and a `<field name="X">` declared more than once. Both are static/text-level checks —
  /// no FCode-internal logic is being reverse-engineered here, just the same two conditions
  /// visible in the source XML itself.
  async validateActive() {
    if (!this.activePath || !this.currentModel) { this.setValidationBanners([]); return; }
    const text = this.currentModel.getValue();
    const dir = this.activePath.substring(0, Math.max(this.activePath.lastIndexOf('\\'), this.activePath.lastIndexOf('/')));
    const messages = [];

    const seenPaths = new Set();
    let m;
    ENTITY_DECL_RE.lastIndex = 0;
    while ((m = ENTITY_DECL_RE.exec(text))) {
      const relPath = m[2];
      if (seenPaths.has(relPath)) continue;
      seenPaths.add(relPath);
      const resolved = resolvePath(dir, relPath.replace(/\//g, '\\'));
      let exists = false;
      try { exists = await window.chrome.webview.hostObjects.host.PathExists(resolved); } catch { exists = false; }
      if (!exists) {
        messages.push(`An error has occurred while opening external entity file: '${resolved}': Could not find file '${resolved}'`);
      }
    }

    // Only guard duplicates that are FCode's own field-name attribute (inside <field
    // name="...">), not every "name=" in the file (attributes, actions, etc. reuse it too).
    const fieldCounts = new Map();
    const fieldRe = /<field\s+name="([^"]+)"/g;
    let fm;
    while ((fm = fieldRe.exec(text))) fieldCounts.set(fm[1], (fieldCounts.get(fm[1]) || 0) + 1);
    if ([...fieldCounts.values()].some((count) => count > 1)) {
      messages.push('Some fields is duplicate in declare.');
    }

    this.setValidationBanners(messages);
  }

  setValidationBanners(messages) {
    const container = document.getElementById('validationBanners');
    if (!container) return;
    container.innerHTML = '';
    messages.forEach((msg, i) => {
      const bar = document.createElement('div');
      bar.className = 'validationBanner';
      const text = document.createElement('span');
      text.className = 'msg';
      text.title = msg;
      text.textContent = msg;
      const dismiss = document.createElement('span');
      dismiss.className = 'dismiss';
      dismiss.textContent = '✕';
      dismiss.onclick = () => bar.remove();
      bar.appendChild(text);
      bar.appendChild(dismiss);
      container.appendChild(bar);
    });
  }

  /// Opens <paramref>path</paramref> in place of whatever's currently shown. Prompts once
  /// if the current file has unsaved changes (there's nowhere else for them to go, since
  /// there's no second tab to keep them in) — same tradeoff a single-document editor makes.
  async openFile(path) {
    if (this.activePath === path) return;
    if (this.dirty && !confirm('File hiện tại có thay đổi chưa lưu. Mở file khác và bỏ thay đổi?')) return;

    let content;
    try {
      content = await window.chrome.webview.hostObjects.host.ReadFile(path);
    } catch (e) {
      alert('Không đọc được file:\n' + path + '\n' + e);
      return;
    }

    if (this.currentModel) this.currentModel.dispose();
    this.currentModel = monaco.editor.createModel(content, detectLanguage(path));
    this.editor.setModel(this.currentModel);
    this.activePath = path;
    this.dirty = false;
    this.bookmarks = new Set(); // bookmarks don't carry over between files
    this.renderBookmarks();
    this.editor.focus();
    // Tells the WinForms host so it can add this to the left "recent files by project"
    // tree, update the breadcrumb/window title, and refresh the status bar's language/
    // modified-time labels — see MainForm.cs's OnFileOpened.
    window.chrome.webview.hostObjects.host.NotifyFileOpened(path);
    this.validateActive();
  }

  async saveActive() {
    if (!this.activePath) return;
    try {
      await window.chrome.webview.hostObjects.host.WriteFile(this.activePath, this.currentModel.getValue());
      this.dirty = false;
      window.chrome.webview.hostObjects.host.NotifyDirtyChanged(this.activePath, false);
    } catch (e) {
      alert('Không ghi được file:\n' + this.activePath + '\n' + e);
    }
  }

  /// "Save As": the file picker itself has to be native (WinForms SaveFileDialog, shown by
  /// MainForm), so this just asks the host for a path and, once chosen, writes there and
  /// switches to editing that new path — same as most editors' Save As behavior.
  async saveActiveAs() {
    if (!this.activePath) return;
    const newPath = await window.chrome.webview.hostObjects.host.ChooseSaveAsPath(this.activePath);
    if (!newPath) return; // user cancelled
    try {
      await window.chrome.webview.hostObjects.host.WriteFile(newPath, this.currentModel.getValue());
    } catch (e) {
      alert('Không ghi được file:\n' + newPath + '\n' + e);
      return;
    }
    this.activePath = null; // force openFile to treat this as a real switch even if newPath happens to equal the old one
    await this.openFile(newPath);
  }

  undo() { this.editor.trigger('toolbar', 'undo'); }
  redo() { this.editor.trigger('toolbar', 'redo'); }

  /// Used by the Hint Code panel's "Insert" action — pastes a saved snippet's code at the
  /// current selection/cursor, same as picking a snippet from FCodeViewer's own Hint Code
  /// list. Replaces the selection if there is one, otherwise inserts at the caret.
  insertTextAtCursor(text) {
    const sel = this.editor.getSelection();
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
    const line = this.editor.getPosition().lineNumber;
    if (this.bookmarks.has(line)) this.bookmarks.delete(line);
    else this.bookmarks.add(line);
    this.renderBookmarks();
  }

  /// Cycles forward through bookmarked lines from the caret's current line, wrapping back
  /// to the first bookmark past the end — matches a typical "next bookmark" toolbar button.
  nextBookmark() {
    if (this.bookmarks.size === 0) return;
    const cur = this.editor.getPosition().lineNumber;
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
    const model = this.editor.getModel();
    const pos = this.editor.getPosition();
    const word = model.getWordAtPosition(pos);
    if (!word) return;

    const declarations = {};
    let m;
    ENTITY_DECL_RE.lastIndex = 0;
    const text = model.getValue();
    while ((m = ENTITY_DECL_RE.exec(text))) declarations[m[1]] = m[2];

    const relPath = declarations[word.word];
    if (!relPath) return;

    const dir = this.activePath.substring(0, Math.max(this.activePath.lastIndexOf('\\'), this.activePath.lastIndexOf('/')));
    const resolved = resolvePath(dir, relPath.replace(/\//g, '\\'));
    try {
      await window.chrome.webview.hostObjects.host.ReadFile(resolved); // existence probe
      await this.openFile(resolved);
    } catch {
      // declared path doesn't resolve to a real file — silently do nothing, same as the
      // Bcode.App version's behavior
    }
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
