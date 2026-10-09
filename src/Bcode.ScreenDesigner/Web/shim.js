// Giả lập phần tối thiểu của BcodeViewer mà "Xem trước Dir" (dirpreview.js) và entity.js cần — để chạy NGUYÊN BẢN code của BcodeViewer ở đây mà không kèm Monaco (14 MB):
//  - hàm tiện ích toàn cục của editor.js / problems.js / completion.js;
//  - monaco.Range;
//  - "editor" + "model" là chuỗi văn bản trong bộ nhớ (executeEdits áp thẳng vào chuỗi, có Undo / Redo).
// Designer chỉ đọc / sửa văn bản trong bộ nhớ; muốn ghi ra file thì bấm "Lưu thiết kế".

function offsetToPosition(text, offset) {
  const upTo = text.slice(0, offset);
  const line = (upTo.match(/\n/g) || []).length + 1;
  const lastNewline = upTo.lastIndexOf('\n');
  return { line, col: offset - lastNewline };
}
function fileNameOf(path) { if (!path) return ''; const i = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/')); return i >= 0 ? path.slice(i + 1) : path; }
function dirNameOf(path) { if (!path) return ''; const i = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/')); return i >= 0 ? path.slice(0, i) : ''; }
function resolvePath(baseDir, relative) {
  const parts = (baseDir + '\\' + relative).split(/[\\/]+/);
  const stack = [];
  for (const part of parts) { if (part === '' || part === '.') continue; if (part === '..') stack.pop(); else stack.push(part); }
  return (baseDir.startsWith('\\\\') ? '\\\\' : '') + stack.join('\\');
}
const XML_BUILTIN_ENTITIES = new Set(['amp', 'lt', 'gt', 'quot', 'apos']);
function looksLikeSql() { return false; }
const JS_HINT_RE = /(?:\bfunction\s+[\w$]+\s*\(|document\.getElementById|setTimeout\s*\(|\$find\(|\.parentForm\b|\bf\._[a-z]|\bthis\._controlBehavior\b)/;

class FakeRange {
  constructor(sl, sc, el, ec) { this.startLineNumber = sl; this.startColumn = sc; this.endLineNumber = el; this.endColumn = ec; }
}
window.monaco = { Range: FakeRange, KeyMod: { CtrlCmd: 1, Alt: 2 }, KeyCode: { KeyF: 3, LeftArrow: 4, F: 3 }, editor: { create() { throw new Error('Designer không có Monaco'); }, setModelLanguage() {} } };

class FakeModel {
  constructor(text) { this.t = String(text || ''); this._starts = null; }
  _s() {
    if (!this._starts) { const a = [0]; for (let i = 0; i < this.t.length; i++) if (this.t.charCodeAt(i) === 10) a.push(i + 1); this._starts = a; }
    return this._starts;
  }
  getValue() { return this.t; }
  setValue(v) { this.t = String(v); this._starts = null; }
  getLineCount() { return this._s().length; }
  getLineContent(n) {
    const s = this._s(); if (n < 1 || n > s.length) return '';
    let e = n < s.length ? s[n] - 1 : this.t.length;
    if (e > s[n - 1] && this.t.charCodeAt(e - 1) === 13) e--;
    return this.t.slice(s[n - 1], e);
  }
  getPositionAt(offset) {
    const s = this._s(); offset = Math.max(0, Math.min(offset, this.t.length));
    let lo = 0, hi = s.length - 1;
    while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (s[mid] <= offset) lo = mid; else hi = mid - 1; }
    return { lineNumber: lo + 1, column: offset - s[lo] + 1 };
  }
  getOffsetAt(p) { const s = this._s(); const l = Math.max(1, Math.min(p.lineNumber, s.length)); return Math.min(this.t.length, s[l - 1] + Math.max(0, p.column - 1)); }
  getValueInRange(r) { return this.t.slice(this.getOffsetAt({ lineNumber: r.startLineNumber, column: r.startColumn }), this.getOffsetAt({ lineNumber: r.endLineNumber, column: r.endColumn })); }
  getEOL() { return this.t.includes('\r\n') ? '\r\n' : '\n'; }
}

class FakeEditor {
  constructor(bcode) { this.bcode = bcode; this.onContent = []; this.onModel = []; this.undoStack = []; this.redoStack = []; }
  onDidChangeModelContent(cb) { this.onContent.push(cb); return { dispose() {} }; }
  onDidChangeModel(cb) { this.onModel.push(cb); return { dispose() {} }; }
  pushUndoStop() {}
  focus() {}
  getPosition() { return { lineNumber: 1, column: 1 }; }
  getSelection() { return new FakeRange(1, 1, 1, 1); }
  setSelection() {}
  revealLineInCenter() {}
  _fire() { this.onContent.forEach((cb) => { try { cb({}); } catch (e) { console.error(e); } }); }
  executeEdits(_source, edits) {
    const m = this.bcode.currentModel; if (!m) return false;
    const items = edits.map((e) => ({
      a: m.getOffsetAt({ lineNumber: e.range.startLineNumber, column: e.range.startColumn }),
      b: m.getOffsetAt({ lineNumber: e.range.endLineNumber, column: e.range.endColumn }),
      text: e.text || '',
    })).sort((x, y) => y.a - x.a);
    let t = m.getValue();
    this.undoStack.push(t); if (this.undoStack.length > 200) this.undoStack.shift(); this.redoStack = [];
    for (const it of items) t = t.slice(0, it.a) + it.text + t.slice(it.b);
    m.setValue(t);
    this._fire();
    return true;
  }
  // Undo / Redo của Designer (nút và Ctrl+Z / Ctrl+Y)
  undo() { const m = this.bcode.currentModel; if (!m || !this.undoStack.length) return false; this.redoStack.push(m.getValue()); m.setValue(this.undoStack.pop()); this._fire(); return true; }
  redo() { const m = this.bcode.currentModel; if (!m || !this.redoStack.length) return false; this.undoStack.push(m.getValue()); m.setValue(this.redoStack.pop()); this._fire(); return true; }
}

class FakeBcode {
  constructor() { this.editor = new FakeEditor(this); this.currentModel = null; this.activePath = ''; }
  /// Nạp văn bản mới (đổi màn hình): reset Undo, báo cho dirpreview vẽ lại.
  setText(text, path) {
    this.activePath = path || '';
    this.currentModel = new FakeModel(text);
    this.editor.undoStack = []; this.editor.redoStack = [];
    this.editor.onModel.forEach((cb) => { try { cb({}); } catch (e) { console.error(e); } });
  }
  /// Sửa văn bản trực tiếp (ô mã XML) — cùng đường với kéo-thả để Undo hoạt động.
  replaceAll(text) {
    const m = this.currentModel; if (!m || m.getValue() === text) return;
    this.editor.undoStack.push(m.getValue()); this.editor.redoStack = [];
    m.setValue(text); this.editor._fire();
  }
  async openFile() { /* Designer không mở file khác */ }
}
