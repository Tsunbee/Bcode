// Hộp chọn nhanh tab: GIỮ Ctrl một lúc (>= 350ms) RỒI bấm Tab thì hiện danh sách các tab đang có trên thanh tab để chọn tab cần đến; bấm Ctrl+Tab ngay thì chuyển tab luôn (không hiện hộp).
//   chuột: rê/bấm một dòng                      bàn phím: ↑ ↓ hoặc Tab / Shift+Tab, rồi Enter — hoặc thả Ctrl nếu đã dùng Tab/mũi tên
//   Esc / bấm ra ngoài: đóng, không đổi tab
// Liệt kê mọi file (cây + đang mở) bằng chính logic đặt tên của BcodeTabs (tabs.js); chọn file đã mở thì kích hoạt tab, chưa mở thì mở qua cây.
// Trang này không có control WinForms nào nhận phím, nên chỉ cần lắng nghe ở đây. Bản tương ứng trong Bcode: MainForm.ShowTabSwitcher.

class BcodeTabSwitcher {
  static HOLD_MS = 350;

  constructor() {
    this.ctrlDownAt = 0;
    this.root = null;
    this.rows = [];
    this.index = 0;
    this.usedTab = false;

    document.addEventListener('keydown', (e) => this.onKeyDown(e), true);
    document.addEventListener('keyup', (e) => this.onKeyUp(e), true);
    for (const name of ['mousedown', 'wheel']) {
      window.addEventListener(name, (e) => {
        if (this.root && name === 'mousedown' && !e.target.closest('.tsBox')) this.close();
        this.cancel();
      }, true);
    }
    window.addEventListener('blur', () => { this.cancel(); this.close(); });
  }

  cancel() { /* không còn bộ đếm: hộp chỉ mở khi bấm Tab sau khi đã giữ Ctrl đủ lâu (xem onKeyDown) */ }

  isOpen() { return !!this.root; }

  onKeyDown(e) {
    if (this.root) { this.onKeyOpen(e); return; }
    if (e.key === 'Control') { if (!e.repeat) this.ctrlDownAt = Date.now(); return; }
    if (e.key === 'Tab' && e.ctrlKey && !e.altKey && !e.metaKey && this.ctrlDownAt && Date.now() - this.ctrlDownAt >= BcodeTabSwitcher.HOLD_MS) {
      // Giữ Ctrl đủ lâu rồi bấm Tab: mở hộp ở tab kế / trước (Shift) và nuốt phím — Ctrl+Tab bấm ngay (chưa đủ lâu) thì để phím đi tiếp tới editor để chuyển tab như thường.
      e.preventDefault(); e.stopImmediatePropagation();
      this.show();
      if (this.root) this.move(e.shiftKey ? -1 : 1);   // đứng ở tab kế / trước; usedTab giữ false → thả Ctrl không tự chuyển (chỉ khi bấm Tab / mũi tên thêm)
    }
  }

  onKeyUp(e) {
    if (e.key !== 'Control') return;
    this.ctrlDownAt = 0;
    if (this.root && this.usedTab) { e.preventDefault(); this.accept(); }
  }

  onKeyOpen(e) {
    const stop = () => { e.preventDefault(); e.stopPropagation(); };
    if (e.key === 'Escape') { stop(); this.close(); }
    else if (e.key === 'Enter') { stop(); this.accept(); }
    else if (e.key === 'ArrowDown' || (e.key === 'Tab' && !e.shiftKey)) { stop(); this.usedTab = true; this.move(1); }
    else if (e.key === 'ArrowUp' || (e.key === 'Tab' && e.shiftKey)) { stop(); this.usedTab = true; this.move(-1); }
    else if (e.key !== 'Control' && e.key !== 'Shift' && e.key !== 'Alt') this.close(); // phím khác (vd Ctrl+F): chỉ đóng hộp rồi để phím đi tiếp tới editor — không nuốt
  }

  /// Danh sách để chọn: MỌI file — các file trong cây (theo thứ tự cây, kể cả khi cây dọc đang hiện) rồi tới file đang mở mà cây không có,
  /// đúng như thanh tab khi ẩn cây. Chữ hiển thị lấy từ chính BcodeTabs (tên trùng thì kèm project / thư mục).
  collect() {
    const tabs = window.bcodeTabs;
    if (!tabs) return [];
    const items = tabs.items(true);
    const label = tabs.labeler(items);
    const bcode = tabs.bcode;
    return items.map((it) => {
      const doc = bcode.docs.get(it.path);
      return {
        path: it.path,
        key: it.key,
        label: label(it),
        active: it.path === bcode.activePath,
        dirty: !!(doc && doc.dirty),
        closed: !doc,
        title: it.path,
        go: () => (doc ? bcode.activateDoc(it.path) : window.bcodeShell.cmd('tree.open', it.path)),
      };
    });
  }

  show() {
    if (this.root) return;
    this.items = this.collect();
    if (!this.items.length) return;
    this.usedTab = false;
    this.index = Math.max(0, this.items.findIndex((t) => t.active));

    const root = document.createElement('div');
    root.className = 'tsOverlay';
    const box = document.createElement('div');
    box.className = 'tsBox';
    const head = document.createElement('div');
    head.className = 'tsHead';
    head.textContent = 'Chuyển tab — ↑ ↓ hoặc Tab để chọn, Enter hoặc bấm chuột để đi, Esc để đóng';
    const list = document.createElement('div');
    list.className = 'tsList';

    this.rows = this.items.map((t, i) => {
      const row = document.createElement('div');
      row.className = 'tsRow' + (t.active ? ' current' : '') + (t.closed ? ' notOpen' : '');
      row.title = t.title;
      const num = document.createElement('span'); num.className = 'tsNum'; num.textContent = String(i + 1);
      const name = document.createElement('span'); name.className = 'tsName'; name.textContent = t.label;
      const mark = document.createElement('span'); mark.className = 'tsMark'; mark.textContent = t.dirty ? '●' : (t.active ? '◆' : '');
      row.append(num, name, mark);
      row.onmousemove = () => { if (this.index !== i) { this.index = i; this.paint(); } };
      row.onclick = (e) => { e.stopPropagation(); this.index = i; this.accept(); };
      list.appendChild(row);
      return row;
    });

    box.append(head, list);
    root.appendChild(box);
    document.body.appendChild(root);
    this.root = root;
    this.paint();
  }

  paint() {
    this.rows.forEach((r, i) => r.classList.toggle('sel', i === this.index));
    const sel = this.rows[this.index];
    if (sel) sel.scrollIntoView({ block: 'nearest' });
  }

  move(step) {
    if (!this.rows.length) return;
    this.index = (this.index + step + this.rows.length) % this.rows.length;
    this.paint();
  }

  accept() {
    const t = this.items && this.items[this.index];
    this.close();
    if (t && t.go) t.go();
  }

  close() {
    if (this.root) { this.root.remove(); this.root = null; }
    this.rows = [];
    this.usedTab = false;
  }
}

window.BcodeTabSwitcher = BcodeTabSwitcher;
window.bcodeTabSwitcher = new BcodeTabSwitcher();
