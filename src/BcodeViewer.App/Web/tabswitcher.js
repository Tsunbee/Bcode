// Hộp chọn nhanh tab (giống hộp Ctrl+Tab của FCodeViewer: 2 cột Header / Path, dòng "Previous:", file nhóm theo dự án + thư mục site):
//   - GIỮ Ctrl một lúc (>= 350ms) RỒI bấm Tab → hiện hộp ngay;
//   - bấm Ctrl+Tab rồi VẪN GIỮ Ctrl (>= 400ms) → tab đã chuyển như thường, rồi hiện hộp (đứng ở tab vừa chuyển tới);
//   - bấm Ctrl+Tab rồi thả ngay → chỉ chuyển tab (không hiện hộp), như trước.
//   chuột: rê/bấm một dòng                      bàn phím: ↑ ↓ hoặc Tab / Shift+Tab, rồi Enter — hoặc thả Ctrl nếu đã dùng Tab/mũi tên
//   Esc / bấm ra ngoài: đóng, không đổi tab
// Liệt kê mọi file (cây + đang mở) bằng chính logic đặt tên của BcodeTabs (tabs.js); chọn file đã mở thì kích hoạt tab, chưa mở thì mở qua cây.
// Trang này không có control WinForms nào nhận phím, nên chỉ cần lắng nghe ở đây. Bản tương ứng trong Bcode: MainForm.ShowTabSwitcher.

class BcodeTabSwitcher {
  static HOLD_MS = 350;
  static HOLD_AFTER_TAB_MS = 400;   // Ctrl+Tab rồi giữ Ctrl bao lâu thì hiện hộp

  constructor() {
    this.ctrlDownAt = 0;
    this.holdTimer = 0;
    this.prevPath = null;   // file đang xem lúc bấm Ctrl+Tab — dòng "Previous:"
    this.root = null;
    this.rows = [];
    this.index = 0;
    this.usedTab = false;

    // Nghe ở WINDOW (pha capture của window chạy trước mọi bộ nghe capture của document): keys.js nghe ở document, gặp Ctrl+Tab là chạy
    // "tab kế" rồi stopImmediatePropagation — nghe ở document thì hộp không bao giờ thấy phím Tab. Ctrl+Tab bấm nhanh vẫn để keys.js xử lý.
    window.addEventListener('keydown', (e) => this.onKeyDown(e), true);
    window.addEventListener('keyup', (e) => this.onKeyUp(e), true);
    window.addEventListener('mousedown', (e) => {
      if (this.root && !e.target.closest('.tsBox')) this.close();
      this.cancel();
    }, true);
    // Hộp đang mở (thường là lúc đang giữ Ctrl): lăn chuột = chọn dòng trên / dưới như Tab / Shift+Tab, thả Ctrl là mở. Phải chặn (preventDefault,
    // listener không passive): Ctrl+lăn chuột trên WebView2 là zoom cả trang — trước đây giữ Ctrl+Tab rồi lăn chuột làm cả app thu nhỏ.
    // Hộp đóng thì không đụng gì: Ctrl+lăn chuột vẫn zoom như cũ.
    this.wheelAcc = 0;
    window.addEventListener('wheel', (e) => {
      if (!this.root) { this.cancel(); return; }
      e.preventDefault();
      e.stopImmediatePropagation();
      // Gộp delta (bàn rê / chuột độ phân giải cao gửi nhiều nhịp nhỏ): đủ ~1 nấc (40px) mới đi 1 dòng.
      this.wheelAcc += e.deltaMode === 1 ? e.deltaY * 40 : e.deltaY;
      while (Math.abs(this.wheelAcc) >= 40) {
        const step = this.wheelAcc > 0 ? 1 : -1;
        this.wheelAcc -= step * 40;
        this.usedTab = true;
        this.move(step);
      }
    }, { capture: true, passive: false });
    window.addEventListener('blur', () => { this.ctrlDownAt = 0; this.cancel(); this.close(); });
  }

  /// Huỷ hẹn giờ "Ctrl+Tab rồi giữ Ctrl" (thả Ctrl, bấm chuột, lăn chuột, cửa sổ mất focus).
  cancel() {
    if (this.holdTimer) { clearTimeout(this.holdTimer); this.holdTimer = 0; }
  }

  isOpen() { return !!this.root; }

  onKeyDown(e) {
    if (this.root) { this.onKeyOpen(e); return; }
    if (e.key === 'Control') { if (!e.repeat) this.ctrlDownAt = Date.now(); return; }
    if (e.key !== 'Tab' || !e.ctrlKey || e.altKey || e.metaKey) return;
    const bcode = window.bcodeTabs && window.bcodeTabs.bcode;
    if (this.ctrlDownAt && Date.now() - this.ctrlDownAt >= BcodeTabSwitcher.HOLD_MS) {
      // Giữ Ctrl đủ lâu rồi bấm Tab: mở hộp ở tab kế / trước (Shift) và nuốt phím — Ctrl+Tab bấm ngay (chưa đủ lâu) thì để phím đi tiếp tới editor để chuyển tab như thường.
      e.preventDefault(); e.stopImmediatePropagation();
      this.cancel();
      this.prevPath = bcode && bcode.activePath;
      this.show();
      if (this.root) this.move(e.shiftKey ? -1 : 1);   // đứng ở tab kế / trước; usedTab giữ false → thả Ctrl không tự chuyển (chỉ khi bấm Tab / mũi tên thêm)
      return;
    }
    // Ctrl+Tab bấm nhanh: để editor chuyển tab như thường; còn giữ Ctrl thêm HOLD_AFTER_TAB_MS thì mở hộp (đứng ở tab vừa tới).
    // Bấm Tab tiếp lúc vẫn giữ Ctrl (chuyển tiếp nhiều tab) thì hẹn lại giờ, "Previous" vẫn là file lúc bắt đầu.
    if (!this.holdTimer) this.prevPath = bcode && bcode.activePath;
    this.cancel();
    this.holdTimer = setTimeout(() => {
      this.holdTimer = 0;
      if (this.ctrlDownAt && !this.root) this.show();
    }, BcodeTabSwitcher.HOLD_AFTER_TAB_MS);
  }

  onKeyUp(e) {
    if (e.key !== 'Control') return;
    this.ctrlDownAt = 0;
    this.cancel();
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
  /// đúng như thanh tab khi ẩn cây. Mỗi file kèm dự án (nhóm cây) và thư mục site (phần trước \App_Data\) để chia nhóm như FCodeViewer;
  /// cột Header = "thư mục cha\tên file" (Dir\Customer.xml), cột Path = đường dẫn đầy đủ.
  collect() {
    const tabs = window.bcodeTabs;
    if (!tabs) return [];
    const bcode = tabs.bcode;
    const entry = (path, extra) => {
      const doc = bcode.docs.get(path);
      const parts = path.split('\\').filter(Boolean);
      return Object.assign({
        path,
        header: parts.slice(-2).join('\\'),
        active: path === bcode.activePath,
        dirty: !!(doc && doc.dirty),
        closed: !doc,
        go: () => (bcode.docs.has(path) ? bcode.activateDoc(path) : window.bcodeShell.cmd('tree.open', path)),
      }, extra);
    };
    return tabs.items(true).map((it) => entry(it.path, { key: it.key, project: it.group || '' }));
  }

  /// Thư mục site của file: phần trước "\App_Data\" (\\172.168.5.14\CustomerPro\FBI\VPMILK\FBISP2422); không có App_Data thì thư mục chứa file.
  static siteOf(path) {
    const i = path.toLowerCase().indexOf('\\app_data\\');
    if (i > 0) return path.slice(0, i);
    const j = path.lastIndexOf('\\');
    return j > 0 ? path.slice(0, j) : path;
  }

  show() {
    if (this.root) return;
    const files = this.collect();
    if (!files.length) return;
    this.usedTab = false;

    // Gom theo dự án → thư mục site, giữ thứ tự xuất hiện (thứ tự cây). File không thuộc App_Data nằm dưới một site của cùng dự án thì vào site đó.
    const groups = [];
    const byKey = new Map();
    for (const f of files) {
      let site = BcodeTabSwitcher.siteOf(f.path);
      const sameProject = groups.filter((g) => g.project === f.project);
      const under = sameProject.find((g) => f.path.toLowerCase().startsWith(g.site.toLowerCase() + '\\'));
      if (under) site = under.site;
      const k = f.project + '|' + site.toLowerCase();
      let g = byKey.get(k);
      if (!g) { g = { project: f.project, site, files: [] }; byKey.set(k, g); groups.push(g); }
      g.files.push(f);
    }

    // Dòng chọn được: "Previous:" (file vừa rời khi bấm Ctrl+Tab) rồi các file theo nhóm.
    const prev = this.prevPath && this.prevPath !== (window.bcodeTabs && window.bcodeTabs.bcode.activePath)
      ? files.find((f) => f.path === this.prevPath) : null;
    this.items = [];
    if (prev) this.items.push(Object.assign({}, prev, { previous: true }));
    for (const g of groups) this.items.push(...g.files);
    this.index = Math.max(0, this.items.findIndex((t) => t.active && !t.previous));

    const root = document.createElement('div');
    root.className = 'tsOverlay';
    const box = document.createElement('div');
    box.className = 'tsBox';
    const cols = document.createElement('div');
    cols.className = 'tsCols';
    cols.append(Object.assign(document.createElement('span'), { textContent: 'Header' }), Object.assign(document.createElement('span'), { textContent: 'Path' }));
    const list = document.createElement('div');
    list.className = 'tsList';

    const rowFor = (t, i) => {
      const row = document.createElement('div');
      row.className = 'tsRow' + (t.active && !t.previous ? ' current' : '') + (t.closed ? ' notOpen' : '') + (t.previous ? ' previous' : '');
      row.title = t.path + (t.dirty ? '\n(chưa lưu)' : '') + (t.closed ? '\n(chưa mở — chọn để mở)' : '');
      const name = document.createElement('span'); name.className = 'tsName';
      name.textContent = (t.previous ? 'Previous: ' : '') + t.header + (t.dirty ? ' ●' : '');
      const path = document.createElement('span'); path.className = 'tsPath'; path.textContent = t.path;
      row.append(name, path);
      row.onmousemove = () => { if (this.index !== i) { this.index = i; this.paint(); } };
      row.onclick = (e) => { e.stopPropagation(); this.index = i; this.accept(); };
      return row;
    };

    this.rows = [];
    let i = 0;
    if (prev) { const r = rowFor(this.items[0], 0); list.appendChild(r); this.rows.push(r); i = 1; }
    for (const g of groups) {
      const gh = document.createElement('div');
      gh.className = 'tsGroup';
      if (g.project) gh.appendChild(Object.assign(document.createElement('b'), { textContent: g.project }));
      gh.appendChild(Object.assign(document.createElement('span'), { textContent: g.site }));
      list.appendChild(gh);
      for (const f of g.files) { const r = rowFor(f, i++); list.appendChild(r); this.rows.push(r); }
    }

    const foot = document.createElement('div');
    foot.className = 'tsHead';
    foot.textContent = 'Giữ Ctrl: Tab / Shift+Tab hoặc ↑ ↓ để chọn, thả Ctrl để đi · Enter / bấm chuột · Esc đóng · ● = chưa lưu, chữ mờ = chưa mở';
    box.append(cols, list, foot);
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
    this.wheelAcc = 0;
  }
}

window.BcodeTabSwitcher = BcodeTabSwitcher;
window.bcodeTabSwitcher = new BcodeTabSwitcher();
