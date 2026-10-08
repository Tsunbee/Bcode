// Phím tắt tuỳ chỉnh + tuỳ chọn giao diện do người dùng khai báo (Settings → Phím tắt / Bố cục).
//
//   window.bcodePrefs — {shortcuts:{id:combo}, toolbarOrder:[], layout:{}, hiddenToolbar:[]}, lưu ở host (ViewerSettings) qua GetUiPrefs/SetUiPrefs.
//   window.bcodeKeys  — bảng chức năng (BCODE_COMMANDS) + phím hiện hành + bộ bắt phím toàn trang.
//
// Bộ bắt phím chạy ở pha capture, đăng ký ngay khi file này nạp nên đứng TRƯỚC mọi bộ lắng nghe khác (editor, Quick Open, shell...):
//   • tổ hợp đang gán cho 1 chức năng → chạy chức năng đó và chặn phím (stopImmediatePropagation) để Monaco không xử lý thêm lần nữa;
//   • tổ hợp là phím MẶC ĐỊNH của 1 chức năng đã bị đổi/bỏ phím → chỉ chặn, để phím cũ không còn tác dụng.
// Khi đang mở hộp thoại (Settings, Quick Open...) thì không can thiệp — hộp thoại tự xử lý phím của nó.

const BCODE_COMMANDS = [
  // group, id, label, default combo, run(bcode)
  ['Tệp', 'file.save', 'Lưu', 'Ctrl+S', (b) => b.saveActive()],
  ['Tệp', 'file.saveAs', 'Lưu thành...', '', (b) => b.saveActiveAs()],
  ['Tệp', 'file.new', 'New from Template...', 'Ctrl+N', (b) => b.openNewFromTemplate()],
  ['Tệp', 'file.quickOpen', 'Mở nhanh file (Quick Open)', 'Ctrl+P', () => window.bcodeQuickOpen && window.bcodeQuickOpen.open()],
  ['Tệp', 'file.history', 'Lịch sử file', 'Ctrl+Shift+H', (b) => window.bcodeHistory.showHistory(b)],
  ['Tệp', 'file.refresh', 'Tải lại file từ đĩa', '', (b) => b.refreshActive()],
  ['Tệp', 'file.saveRun', 'Lưu rồi mở menu của file trên trình duyệt', 'F5', (b, e) => { if (!(e && e.repeat)) b.saveAndRunMenu(); }],
  ['Tệp', 'app.settings', 'Mở Settings', '', (b) => b.openSettings()],

  ['Tab', 'tab.close', 'Đóng tab hiện tại', 'Ctrl+W', (b) => b.closeActive()],
  ['Tab', 'tab.next', 'Tab kế tiếp', 'Ctrl+Tab', (b) => b.cycleTab(1)],
  ['Tab', 'tab.prev', 'Tab trước đó', 'Ctrl+Shift+Tab', (b) => b.cycleTab(-1)],
  ['Tab', 'view.split', 'Tách đôi khung soạn thảo', 'Ctrl+\\', (b) => b.toggleSplit()],

  ['Soạn thảo', 'edit.undo', 'Hoàn tác (nút Undo)', '', (b) => b.undo()],
  ['Soạn thảo', 'edit.redo', 'Làm lại (nút Redo)', '', (b) => b.redo()],
  ['Soạn thảo', 'edit.comment', 'Comment / bỏ comment dòng', 'Ctrl+/', (b) => b.toggleComment()],
  ['Soạn thảo', 'edit.duplicate', 'Nhân đôi dòng / đoạn đang chọn', 'Ctrl+D', (b) => b.editor.getAction('bcode.duplicateLine')?.run()],
  ['Soạn thảo', 'edit.nextMatch', 'Thêm con trỏ ở kết quả giống tiếp theo', 'Ctrl+Shift+D', (b) => b.editor.getAction('bcode.addSelectionToNextMatch')?.run()],
  ['Soạn thảo', 'edit.bookmark', 'Đặt / bỏ bookmark', '', (b) => b.toggleBookmark()],
  ['Soạn thảo', 'edit.nextBookmark', 'Tới bookmark kế tiếp', '', (b) => b.nextBookmark()],
  ['Soạn thảo', 'edit.hint', 'Hint Code (thư viện snippet)', '', (b) => b.openHintCode()],
  ['Soạn thảo', 'edit.inlineAi', 'AI viết code theo chỉ dẫn (inline)', 'Ctrl+I', (b) => window.bcodeDialogs.showInlineGenerate(b)],
  ['Soạn thảo', 'edit.translateHeaders', 'Dịch caption <header v e>', 'Ctrl+Alt+T', (b) => window.bcodeDialogs.showTranslateHeaders(b)],

  ['Điều hướng', 'nav.goto', 'Go to (tag / chi tiết controller)', 'Ctrl+G', () => window.bcodeGoto && window.bcodeGoto.open()],
  ['Điều hướng', 'nav.peek', 'Xem trước nơi khai báo (peek)', 'F12', (b) => b.jumpToEntityAtCaret('peek')],
  ['Điều hướng', 'nav.go', 'Đi tới file / nơi khai báo', 'Ctrl+F12', (b) => b.jumpToEntityAtCaret('go')],
  ['Điều hướng', 'nav.function', 'Đi tới function tại con trỏ', 'F11', (b) => b.gotoFunctionAtCaret()],
  ['Điều hướng', 'nav.references', 'Tìm nơi sử dụng (toàn project)', 'Shift+F12', () => window.bcodeOutline.findReferencesAtCaret()],

  ['Bảng & khung', 'panel.search', 'Tìm trong toàn bộ project', 'Ctrl+Shift+F', (b) => window.bcodeSearch.open(b.editor.getModel()?.getValueInRange(b.editor.getSelection()) || '')],
  ['Bảng & khung', 'panel.problems', 'Hiện/ẩn bảng Problems', 'Ctrl+Shift+M', () => window.bcodeProblems.toggle()],
  ['Bảng & khung', 'panel.outline', 'Hiện/ẩn Outline', 'Ctrl+Shift+U', () => window.bcodeOutline.toggle()],
  ['Bảng & khung', 'panel.dirPreview', 'Xem trước màn hình Dir / mail mẫu', 'Alt+P', (b) => b.toggleDirPreview()],
  ['Bảng & khung', 'panel.sidebar', 'Hiện/ẩn cây file bên trái', 'Ctrl+B', () => window.bcodeShell && window.bcodeShell.toggleSidebar()],
  ['Bảng & khung', 'panel.menubar', 'Hiện/ẩn thanh menu + toolbar', '', () => window.bcodeShell && window.bcodeShell.toggleChrome()],
  ['Bảng & khung', 'panel.claude', 'Hiện/ẩn khung Claude', '', () => window.bcodeShell && window.bcodeShell.cmd('toggleClaude')],
  ['Bảng & khung', 'panel.gemini', 'Hiện/ẩn khung Gemini', '', () => window.bcodeShell && window.bcodeShell.cmd('toggleGemini')],
  ['Bảng & khung', 'panel.attach', 'Đính kèm file đang mở vào chat AI', '', () => window.bcodeShell && window.bcodeShell.cmd('attachFile')],

  ['Công cụ', 'tool.runSql', 'Chạy SQL tại con trỏ', 'Ctrl+Enter', () => window.bcodeSqlRun.run()],
  ['Công cụ', 'tool.clearStructure', 'Clear Structure App', '', () => window.bcodeShell && window.bcodeShell.cmd('clearStructure')],
  ['Công cụ', 'tool.refreshWebConfig', 'Reset WebConfig', '', () => window.bcodeShell && window.bcodeShell.cmd('refreshWebConfig')],
].map(([group, id, label, def, run]) => ({ group, id, label, def, run }));

const BCODE_RESERVED = new Set(['Ctrl+C', 'Ctrl+V', 'Ctrl+X', 'Ctrl+Z', 'Ctrl+Y', 'Ctrl+A', 'Alt+F4', 'Ctrl+Alt+Delete', 'Ctrl+Shift+Escape']);
const BCODE_NAMED_KEYS = { Tab: 'Tab', Enter: 'Enter', NumpadEnter: 'Enter', Space: 'Space', Backspace: 'Backspace', Delete: 'Delete', Insert: 'Insert',
  Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
  Backquote: '`', Minus: '-', Equal: '=', BracketLeft: '[', BracketRight: ']', Backslash: '\\', Semicolon: ';', Quote: "'", Comma: ',', Period: '.', Slash: '/' };

/// Tuỳ chọn người dùng — một bản duy nhất cho cả trang, nạp từ host trước khi dựng khung (index.html).
class BcodePrefs {
  constructor() {
    this.data = { shortcuts: {}, toolbarOrder: [], layout: {}, hiddenToolbar: [] };
  }

  get host() {
    return window.chrome && window.chrome.webview ? window.chrome.webview.hostObjects.host : null;
  }

  async load() {
    try {
      const raw = this.host ? await this.host.GetUiPrefs() : null;
      if (raw) {
        const d = JSON.parse(raw);
        this.data = {
          shortcuts: d.shortcuts || {}, toolbarOrder: d.toolbarOrder || [], layout: d.layout || {}, hiddenToolbar: d.hiddenToolbar || [],
        };
      }
    } catch { /* host chưa sẵn sàng — dùng mặc định */ }
  }

  async save(partial) {
    Object.assign(this.data, partial);
    try { if (this.host) await this.host.SetUiPrefs(JSON.stringify(this.data)); } catch { /* host đóng */ }
  }

  layout(key, fallback) { const v = this.data.layout[key]; return v === undefined ? fallback : v; }
  layoutOn(key, fallback = true) { const v = this.data.layout[key]; return v === undefined ? fallback : v !== '0'; }
}

class BcodeKeys {
  constructor() {
    this.recording = false;
    this.rebuild();
  }

  get bcode() { return window.bcodeViewer; }
  find(id) { return BCODE_COMMANDS.find((c) => c.id === id); }

  /// Phím đang dùng của 1 chức năng ('' = chưa gán / đã bỏ).
  get(id) {
    const def = this.find(id);
    if (!def) return '';
    const ov = window.bcodePrefs ? window.bcodePrefs.data.shortcuts : {};
    if (Object.prototype.hasOwnProperty.call(ov, id)) return ov[id] === '' ? '' : (this.normalize(ov[id]) || def.def);
    return def.def;
  }

  /// Dựng lại bảng tổ hợp → chức năng và tập phím mặc định cần chặn; gọi sau mỗi lần đổi phím.
  rebuild() {
    this.map = new Map();
    for (const c of BCODE_COMMANDS) { const k = this.get(c.id); if (k) this.map.set(k, c.id); }
    this.blocked = new Set();
    for (const c of BCODE_COMMANDS) if (c.def && this.get(c.id) !== c.def && !this.map.has(c.def)) this.blocked.add(c.def);
  }

  comboOf(e) {
    const c = e.code;
    let k = null;
    if (/^Key[A-Z]$/.test(c)) k = c.charAt(3);
    else if (/^Digit[0-9]$/.test(c)) k = c.charAt(5);
    else if (/^F([1-9]|1[0-9]|2[0-4])$/.test(c)) k = c;
    else if (BCODE_NAMED_KEYS[c]) k = BCODE_NAMED_KEYS[c];
    if (!k || e.metaKey) return null;
    const m = [];
    if (e.ctrlKey) m.push('Ctrl');
    if (e.altKey) m.push('Alt');
    if (e.shiftKey) m.push('Shift');
    m.push(k);
    return m.join('+');
  }

  /// "ctrl + shift + k" → "Ctrl+Shift+K"; null nếu không hợp lệ.
  normalize(combo) {
    if (!combo || !String(combo).trim()) return null;
    let ctrl = false, alt = false, shift = false, key = null;
    const parts = String(combo).split('+').map((p) => p.trim());
    for (const p of parts) {
      const l = p.toLowerCase();
      if (l === 'ctrl' || l === 'control') ctrl = true;
      else if (l === 'alt') alt = true;
      else if (l === 'shift') shift = true;
      else if (key !== null || !p) return null;
      else if (p.length === 1) key = /[a-z]/i.test(p) ? p.toUpperCase() : p;
      else if (/^f([1-9]|1\d|2[0-4])$/i.test(p)) key = p.toUpperCase();
      else {
        const named = Object.values(BCODE_NAMED_KEYS).find((n) => n.toLowerCase() === l);
        if (!named) return null;
        key = named;
      }
    }
    if (key === null) return null;
    return [ctrl && 'Ctrl', alt && 'Alt', shift && 'Shift', key].filter(Boolean).join('+');
  }

  /// Lý do không dùng được tổ hợp này làm phím tắt (rỗng = dùng được).
  problem(combo) {
    if (BCODE_RESERVED.has(combo)) return combo + ' là phím hệ thống / sửa văn bản, không dùng làm phím tắt.';
    const parts = combo.split('+');
    const key = parts[parts.length - 1];
    const isF = /^F\d+$/.test(key);
    const hasCtrlAlt = parts.includes('Ctrl') || parts.includes('Alt');
    if (!isF && !hasCtrlAlt) return 'Phím này phải đi cùng Ctrl hoặc Alt (chỉ phím F1–F24 được dùng riêng).';
    return '';
  }

  /// Chức năng đang giữ tổ hợp này (hoặc null).
  owner(combo, exceptId) {
    for (const c of BCODE_COMMANDS) if (c.id !== exceptId && this.get(c.id) === combo) return c;
    return null;
  }

  handle(e) {
    if (this.recording || e.isComposing || !window.bcodeViewer) return;
    if (document.querySelector('.dlgOverlay, .qoOverlay, .tsBox')) return; // đang có hộp thoại: để nó tự xử lý
    const combo = this.comboOf(e);
    if (!combo) return;
    const id = this.map.get(combo);
    if (id) {
      e.preventDefault();
      e.stopImmediatePropagation();
      try { this.find(id).run(this.bcode, e); } catch (err) { if (window.bcodeShowPageError) window.bcodeShowPageError('Lỗi chạy phím tắt ' + combo + ': ' + err); }
    } else if (this.blocked.has(combo)) {
      e.preventDefault();
      e.stopImmediatePropagation();
    }
  }
}

window.bcodePrefs = new BcodePrefs();
window.bcodeKeys = new BcodeKeys();
// Đăng ký NGAY (trước editor.js, quickopen.js...) để đứng đầu hàng các bộ lắng nghe pha capture.
document.addEventListener('keydown', (e) => window.bcodeKeys.handle(e), true);
