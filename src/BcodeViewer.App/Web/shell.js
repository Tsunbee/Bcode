// Khung ứng dụng vẽ trong trang: thanh menu, thanh công cụ, đường dẫn file và cây file gần đây bên trái —
// thay cho MenuStrip / ToolStrip / breadcrumb / TreeView của WinForms để cả cửa sổ cùng một theme.
//
// Mô hình cây vẫn do MainForm giữ (RefreshProjectTree, trạng thái thu gọn lưu trong settings, đóng file đang
// mở trước khi xoá khỏi danh sách...): host đẩy bản JSON sang qua bcodeViewer.shellTree(), trang chỉ vẽ và gửi
// lệnh ngược lại bằng host.ShellCommand(cmd, arg). Lệnh của editor (Save, Undo, Problems...) gọi thẳng
// window.bcodeViewer như các phím tắt vẫn làm.

// Thanh công cụ MẶC ĐỊNH (thứ tự, vạch ngăn) — người dùng ghim / nhóm / sắp xếp lại ở Web/toolbar.js. icon = dùng khi kiểu nút là "icon",
// cmd = chức năng tương ứng trong bảng phím tắt (keys.js) để hiện phím hiện hành trong menu nhóm.
const SHELL_TOOLBAR = [
  { text: 'Save', icon: '💾', cmd: 'file.save', run: (b) => b.saveActive(), title: 'Lưu (Ctrl+S)' },
  { text: 'Save As', icon: '📝', cmd: 'file.saveAs', run: (b) => b.saveActiveAs() },
  { text: '⚠ Check', icon: '⚠', cmd: 'tool.checkField', run: () => window.bcodeCheckField && window.bcodeCheckField.toggle(), title: 'Check Field — đối chiếu khai báo <fields> với <views> của Grid / Dir (thiếu, trùng), copy XML của view' },
  { sep: true },
  { text: 'Undo', icon: '↶', cmd: 'edit.undo', run: (b) => b.undo(), title: 'Hoàn tác (Ctrl+Z)' },
  { text: 'Redo', icon: '↷', cmd: 'edit.redo', run: (b) => b.redo(), title: 'Làm lại (Ctrl+Y)' },
  { sep: true },
  { text: 'Comment', icon: '//', cmd: 'edit.comment', run: (b) => b.toggleComment(), title: 'Comment/bỏ comment dòng (Ctrl+/)' },
  { sep: true },
  { text: 'Bookmark', icon: '🔖', cmd: 'edit.bookmark', run: (b) => b.toggleBookmark() },
  { text: '● Next', icon: '⤵', cmd: 'edit.nextBookmark', run: (b) => b.nextBookmark(), title: 'Tới bookmark kế tiếp' },
  { sep: true },
  { text: 'Refresh', icon: '⟳', cmd: 'file.refresh', run: (b) => b.refreshActive(), title: 'Tải lại file từ đĩa' },
  { sep: true },
  { text: 'Hint', icon: '💡', cmd: 'edit.hint', run: (b) => b.openHintCode(), title: 'Hint Code — thư viện snippet' },
  { sep: true },
  { text: 'Tìm project', icon: '🔍', cmd: 'panel.search', run: (b) => b.openSearch(), title: 'Tìm trong toàn bộ project (Ctrl+Shift+F)' },
  { text: 'Problems', icon: '⛔', cmd: 'panel.problems', run: (b) => b.toggleProblemsPanel(), title: 'Bảng lỗi/cảnh báo (Ctrl+Shift+M)' },
  { text: 'Outline', icon: '☰', cmd: 'panel.outline', run: (b) => b.toggleOutlinePanel(), title: 'Cấu trúc file (Ctrl+Shift+U)' },
  { text: 'Xem Dir', icon: '🖼', cmd: 'panel.dirPreview', run: (b) => b.toggleDirPreview(), title: 'Xem trước màn hình Dir (Alt+P)' },
  { text: 'Chạy SQL', icon: '▶', cmd: 'tool.runSql', run: (b) => b.runSql(), title: 'Chạy câu SQL tại con trỏ trên WS Bcode đang chọn (Ctrl+Enter)' },
  { text: 'Tách đôi', icon: '◫', cmd: 'view.split', run: (b) => b.toggleSplit(), title: 'Tách đôi khung soạn thảo (Ctrl+\\)' },
  { text: 'Wrap', icon: '↩', cmd: 'view.wrap', run: (b) => b.toggleWordWrap(), toggle: (b) => b.isWordWrap(), title: 'Tự ngắt dòng dài theo bề ngang khung (Alt+Z) — nhẹ hơn tự thu nhỏ cỡ chữ' },
  { sep: true },
  { text: '🤖 Claude Sidebar', icon: '🤖', cmd: 'panel.claude', host: 'toggleClaude' },
  { text: '✨ Gemini Sidebar', icon: '✨', cmd: 'panel.gemini', host: 'toggleGemini' },
  { text: '📎 Đính kèm file', icon: '📎', cmd: 'panel.attach', host: 'attachFile', title: 'Đính kèm file đang mở vào ô chat của sidebar AI đang hiện' },
  { text: 'Clear Structure', icon: '🧹', cmd: 'tool.clearStructure', host: 'clearStructure', title: 'Xoá file trong Controllers\\Structure\\App' },
  { text: 'Reset WebConfig', icon: '♻', cmd: 'tool.refreshWebConfig', host: 'refreshWebConfig', title: 'Chạm web.config để IIS nạp lại site' },
  { text: 'Open WebConfig', icon: '🌐', cmd: 'tool.openWebConfig', host: 'openWebConfig', title: 'Mở web.config của site (thư mục chứa App_Data của file đang mở) thành 1 tab' },
];

class BcodeShell {
  constructor(bcode) {
    this.bcode = bcode;
    this.host = window.chrome && window.chrome.webview ? window.chrome.webview.hostObjects.host : null;
    this.tree = { header: '', nodes: [], activePath: null };
    this.openMenu = null;
    this.autoHide = this.load('shell.autoHide') === '1';

    this.top = document.getElementById('shellTop');
    this.sidebar = document.getElementById('shellSidebar');
    this.resizer = document.getElementById('shellSidebarResizer');
    this.buildTop();
    this.buildSidebar();
    this.applyAutoHide();
    this.applyLayout();

    document.addEventListener('mousedown', (e) => {
      // Nút nhóm của thanh công cụ tự đóng/mở menu của nó khi bấm (toolbar.js) — như tiêu đề menu.
      if (this.openMenu && !e.target.closest('.shMenu') && !e.target.closest('.shMenuTitle, .shTbGroup')) this.closeMenu();
    }, true);
    window.addEventListener('blur', () => this.closeMenu());
    document.addEventListener('keydown', (e) => this.onKey(e), true);

    this.refreshState();
  }

  // ---- helpers -------------------------------------------------------------------------------

  /// Phím hiện hành của 1 chức năng để hiện trên menu (theo bảng phím tắt tuỳ chỉnh).
  k(id) { return window.bcodeKeys ? window.bcodeKeys.get(id) : ''; }

  cmd(name, arg) { try { this.host && this.host.ShellCommand(name, arg || ''); } catch { /* host gone */ } }
  load(key) { try { return localStorage.getItem(key); } catch { return null; } }
  save(key, value) { try { localStorage.setItem(key, value); } catch { /* storage blocked */ } }

  async refreshState() {
    try {
      this.state = JSON.parse(await this.host.GetShellState());
      // Nút đã bỏ ghim: host là bản gốc; toolbar.js đọc từ bcodePrefs (cùng ViewerSettings.HiddenToolbarItems).
      if (window.bcodePrefs && Array.isArray(this.state.hiddenToolbar)) window.bcodePrefs.data.hiddenToolbar = this.state.hiddenToolbar;
    } catch { this.state = null; }
    this.renderToolbar();
  }

  onKey(e) {
    if (e.key === 'Escape' && this.openMenu) { e.preventDefault(); this.closeMenu(); return; }
    // Ctrl+N, Ctrl+B... do bảng phím tắt tuỳ chỉnh xử lý (keys.js).
  }

  // ---- menu bar ------------------------------------------------------------------------------

  menus() {
    const b = this.bcode;
    const st = this.state || { themes: { builtIn: [], custom: [] } };
    const themeItems = (list, kindSuffix) => list.map((t) => ({
      label: kindSuffix ? `${t.name}  (${t.isDark ? 'tối' : 'sáng'})` : t.name,
      checked: !st.themes.followSystem && st.themes.current === t.id,
      run: () => this.cmd('theme', t.id),
    }));
    const theme = [
      { header: 'Tối' }, ...themeItems(st.themes.builtIn.filter((t) => t.isDark)),
      { sep: true },
      { header: 'Sáng' }, ...themeItems(st.themes.builtIn.filter((t) => !t.isDark)),
      { sep: true },
    ];
    if (st.themes.custom.length) {
      theme.push({ header: 'Đã nhập (VS Code / Fcode)' }, ...themeItems(st.themes.custom, true));
      theme.push({ label: 'Xoá theme đã nhập', submenu: st.themes.custom.map((t) => ({ label: t.name, run: () => this.cmd('themeDelete', t.id) })) });
    }
    theme.push(
      { label: 'Nhập theme (VS Code .json / .vsix, Fcode .xml)...', run: () => this.cmd('themeImport') },
      { label: 'Mở thư mục theme đã nhập', run: () => this.cmd('themeFolder') },
      { sep: true },
      { label: 'Theo Windows (sáng/tối)', checked: !!st.themes.followSystem, run: () => this.cmd('themeSystem') },
    );

    // Tick = ghim (hiện trên thanh); nút chưa ghim, nhóm, kiểu nút, vị trí thanh: Web/toolbar.js.
    const toolbarItems = this.tb.gearItems();

    return [
      { title: 'File', items: [
        { label: 'New from Template...', shortcut: this.k('file.new'), run: () => b.openNewFromTemplate() },
        { sep: true },
        { label: 'Mở nhanh file...', shortcut: this.k('file.quickOpen'), run: () => window.bcodeQuickOpen && window.bcodeQuickOpen.open() },
        { label: 'Lịch sử file...', shortcut: this.k('file.history'), run: () => b.openHistory() },
        { label: 'Lịch sử cả dự án...', shortcut: this.k('file.historyProject'), run: () => window.bcodeHistory.showHistory(b, 'project') },
        { sep: true },
        { label: 'Settings...', run: () => b.openSettings() },
        { label: 'Phím tắt...', run: () => b.openSettings('keys') },
      ] },
      { title: 'View', items: [
        { label: 'Tìm trong project...', shortcut: this.k('panel.search'), run: () => b.openSearch() },
        { label: 'Bảng Problems', shortcut: this.k('panel.problems'), run: () => b.toggleProblemsPanel() },
        { label: 'Xem trước Dir', shortcut: this.k('panel.dirPreview'), run: () => b.toggleDirPreview() },
        { label: 'Outline', shortcut: this.k('panel.outline'), run: () => b.toggleOutlinePanel() },
        { label: 'Chạy SQL tại con trỏ', shortcut: this.k('tool.runSql'), run: () => b.runSql() },
        { sep: true },
        { label: 'Tách đôi khung soạn thảo', shortcut: this.k('view.split'), run: () => b.toggleSplit() },
        { label: 'Tìm nơi sử dụng', shortcut: this.k('nav.references'), run: () => b.findReferences() },
        { sep: true },
        { label: 'Show Vertical Tabpage', shortcut: this.k('panel.sidebar'), checked: !this.sidebarHidden(), run: () => this.toggleSidebar() },
        { label: 'Tự ẩn Menu/Toolbar', checked: this.autoHide, run: () => { this.autoHide = !this.autoHide; this.save('shell.autoHide', this.autoHide ? '1' : '0'); this.applyAutoHide(); } },
      ] },
      { title: 'Actions', items: [
        { label: 'Clear Structure App', shortcut: this.k('tool.clearStructure'), run: () => this.cmd('clearStructure') },
        { label: 'Open Web.config', shortcut: this.k('tool.openWebConfig'), run: () => this.cmd('openWebConfig') },
        { label: 'Refresh Web.config', shortcut: this.k('tool.refreshWebConfig'), run: () => this.cmd('refreshWebConfig') },
        { sep: true },
        { label: 'Từ điển dịch (Việt → Anh)...', shortcut: this.k('tool.translateDict'), run: () => window.bcodeDialogs.showTranslateDictionary() },
      ] },
      { title: 'Help', items: [
        { label: 'Giới thiệu...', run: () => this.showAbout() },
        // Web/perfwatch.js: mỗi lần trang bị chặn >= 100 ms ghi 1 dòng (lúc đang làm gì) — thấy giật thì gửi file này.
        { label: 'Log độ mượt (viewer-perf.log)...', run: () => window.bcodeNotify('OpenPerfLog') },
      ] },
      { title: 'Theme', items: theme, refresh: true },
      { title: '⚙ Toolbar', items: toolbarItems, refresh: true },
    ];
  }

  buildTop() {
    this.top.textContent = '';
    this.menubar = document.createElement('div');
    this.menubar.className = 'shMenubar';
    this.toolbar = document.createElement('div');
    this.toolbar.className = 'shToolbar';
    // Ghim / nhóm / kéo thả / chuột phải / vị trí thanh: Web/toolbar.js.
    this.tb = new BcodeToolbar(this);
    this.breadcrumb = document.createElement('div');
    this.breadcrumb.className = 'shBreadcrumb';
    this.top.append(this.menubar, this.toolbar, this.breadcrumb);

    ['File', 'View', 'Actions', 'Help', 'Theme', '⚙ Toolbar'].forEach((title, i) => {
      const el = document.createElement('div');
      el.className = 'shMenuTitle';
      el.textContent = title;
      el.addEventListener('mousedown', (e) => {
        e.preventDefault();
        if (this.openMenu && this.openMenu.index === i) this.closeMenu();
        else this.showMenu(el, i);
      });
      el.addEventListener('mouseenter', () => { if (this.openMenu && this.openMenu.index !== i) this.showMenu(el, i); });
      this.menubar.appendChild(el);
    });
    // Góc phải thanh menu: nút 💡 Hint (Hint Code — thư viện snippet), như nút Hint ở góc phải của Bcode. Luôn hiện, không phụ thuộc ghim/nhóm của toolbar.
    this.menuRight = document.createElement('div');
    this.menuRight.className = 'shMenuRight';
    const hint = document.createElement('div');
    hint.className = 'shMenuHint';
    hint.innerHTML = '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round" '
      + 'd="M8 1.6a4.4 4.4 0 0 0-2.6 7.95c.45.33.7.8.7 1.3v.75h3.8v-.75c0-.5.25-.97.7-1.3A4.4 4.4 0 0 0 8 1.6z"/><path fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" d="M6.3 13.4h3.4M6.8 14.9h2.4"/></svg><span>Hint</span>';
    hint.addEventListener('mousedown', (e) => e.preventDefault());   // giữ focus trong editor
    hint.addEventListener('mouseenter', () => { const k = this.k('edit.hint'); hint.title = 'Hint Code — thư viện snippet' + (k ? ` (${k})` : ''); });
    hint.onclick = () => this.bcode.openHintCode();
    this.menuRight.appendChild(hint);
    this.menubar.appendChild(this.menuRight);
    this.renderToolbar();
  }

  /// Chuột phải chỗ trống của thanh công cụ: mở menu ⚙ Toolbar tại con trỏ (như trước).
  showToolbarMenu(x, y) {
    const title = [...this.menubar.children].find((t) => t.classList.contains('shMenuTitle') && t.textContent === '⚙ Toolbar');
    if (title) this.showMenu(title, 5, x, y);
  }

  /// Menu nổi tại (x, y) — chuột phải cây file, nút trên thanh công cụ, menu nhóm. <titleEl> (nếu có) được tô "open" khi menu đang mở.
  showMenuAt(items, x, y, titleEl) {
    this.closeMenu();
    const menu = this.renderMenu(items);
    document.body.appendChild(menu);
    menu.style.left = Math.max(0, Math.min(x, window.innerWidth - menu.offsetWidth - 4)) + 'px';
    menu.style.top = Math.max(0, Math.min(y, window.innerHeight - menu.offsetHeight - 4)) + 'px';
    const el = titleEl || document.createElement('span');
    el.classList.add('open');
    this.openMenu = { index: -1, menu, titleEl: el };
    return menu;
  }

  async showMenu(titleEl, index, x, y) {
    this.closeMenu();
    const def0 = this.menus()[index];
    if (def0.refresh) await this.refreshState(); // theme / toolbar state may have changed
    const def = this.menus()[index];
    const menu = this.renderMenu(def.items);
    document.body.appendChild(menu);
    const r = titleEl.getBoundingClientRect();
    const left = x !== undefined ? x : r.left;
    const top = y !== undefined ? y : r.bottom;
    menu.style.left = Math.max(0, Math.min(left, window.innerWidth - menu.offsetWidth - 4)) + 'px';
    menu.style.top = Math.max(0, Math.min(top, window.innerHeight - menu.offsetHeight - 4)) + 'px';
    titleEl.classList.add('open');
    this.openMenu = { index, menu, titleEl };
  }

  closeMenu() {
    if (!this.openMenu) return;
    this.openMenu.menu.remove();
    this.openMenu.titleEl.classList.remove('open');
    this.openMenu = null;
  }

  renderMenu(items) {
    const menu = document.createElement('div');
    menu.className = 'shMenu';
    for (const it of items) {
      if (it.sep) { menu.appendChild(Object.assign(document.createElement('div'), { className: 'shSep' })); continue; }
      const row = document.createElement('div');
      if (it.header) { row.className = 'shHeader'; row.textContent = it.header; menu.appendChild(row); continue; }
      row.className = 'shItem';
      const check = document.createElement('span');
      check.className = 'shCheck';
      check.textContent = it.checked ? '✓' : '';
      const label = document.createElement('span');
      label.className = 'shLabel';
      label.textContent = it.label;
      const sc = document.createElement('span');
      sc.className = 'shShortcut';
      sc.textContent = it.submenu ? '▸' : (it.shortcut || '');
      row.append(check, label, sc);
      if (it.submenu) {
        const sub = this.renderMenu(it.submenu);
        sub.classList.add('shSubmenu');
        row.appendChild(sub);
        row.addEventListener('mouseenter', () => {
          // position: fixed theo toạ độ màn hình — menu cha có overflow-y (cuộn khi dài) nên menu con absolute bên trong bị cắt mất.
          sub.style.display = 'block';
          sub.style.position = 'fixed';
          const rr = row.getBoundingClientRect();
          sub.style.left = Math.max(0, rr.right + sub.offsetWidth > window.innerWidth ? rr.left - sub.offsetWidth : rr.right) + 'px';
          sub.style.top = Math.max(0, Math.min(rr.top - 5, window.innerHeight - sub.offsetHeight - 4)) + 'px';
        });
        row.addEventListener('mouseleave', () => { sub.style.display = 'none'; });
      } else {
        // Chuột phải 1 mục (menu nhóm của thanh công cụ: đưa ra thanh, chuyển nhóm, bỏ ghim...).
        if (it.context) row.addEventListener('contextmenu', (e) => { e.preventDefault(); e.stopPropagation(); it.context(e); });
        row.addEventListener('click', (e) => {
          e.stopPropagation();
          if (it.keepOpen) {
            it.run();
            if ('checked' in it) { it.checked = !it.checked; check.textContent = it.checked ? '✓' : ''; }
            if (it.label === 'Hiện tất cả') menu.querySelectorAll('.shCheck').forEach((c) => { c.textContent = '✓'; });
            return;
          }
          this.closeMenu();
          it.run();
        });
      }
      menu.appendChild(row);
    }
    return menu;
  }

  async showAbout() {
    let a = {};
    try { a = JSON.parse(await this.host.GetAboutInfo()); } catch { /* no host */ }
    const lines = [a.description, '', a.version && 'Phiên bản: ' + a.version, a.build && 'Build: ' + a.build,
      a.authors && 'Tác giả: ' + a.authors, a.copyright].filter((x) => x !== undefined && x !== null && x !== false);
    window.bcodeUi.alert(lines.join('\n').trim(), { title: 'Giới thiệu — ' + (a.product || 'BcodeViewer'), kind: 'info' });
  }

  // ---- toolbar -------------------------------------------------------------------------------

  /// Thứ tự nút trên thanh (đã chuẩn hoá, gồm "grp:<id>" của nhóm) — Settings → Bố cục dùng. Chi tiết token: Web/toolbar.js (BcodeTbModel).
  defaultTokens() { return BcodeTbModel.defaults(); }
  toolbarTokens() { return this.tb.model().order; }
  tokenLabel(t) { return BcodeTbModel.label(t, this.tb.model()); }

  /// Vẽ lại thanh công cụ (vd nút Wrap đổi sáng/tối theo trạng thái — editor.js gọi).
  renderToolbar() { if (this.tb) this.tb.render(); }

  // ---- bố cục do người dùng chọn (Settings → Bố cục) -------------------------------------------

  /// Áp tuỳ chọn bố cục: ẩn/hiện menu, toolbar, đường dẫn, thanh tab; cây file bên trái hay bên phải; độ rộng cây.
  applyLayout() {
    const P = window.bcodePrefs;
    if (!P) return;
    const show = (el, on) => { if (el) el.style.display = on ? '' : 'none'; };
    show(this.menubar, P.layoutOn('menubar'));
    show(this.toolbar, P.layoutOn('toolbar'));
    show(this.breadcrumb, P.layoutOn('breadcrumb'));
    show(document.getElementById('tabStrip'), P.layoutOn('tabstrip'));
    // Thanh công cụ trên (mặc định) / cùng hàng menu / dưới cùng cửa sổ — Settings → Bố cục hoặc chuột phải thanh công cụ.
    const tbPos = this.tb.applyPosition();
    // Cả khung trên cùng trống thì không để lại dải rỗng.
    if (this.top) this.top.style.display = (P.layoutOn('menubar') || (P.layoutOn('toolbar') && tbPos !== 'bottom') || P.layoutOn('breadcrumb')) ? '' : 'none';
    const right = P.layout('sidebarSide', 'left') === 'right';
    document.getElementById('main').classList.toggle('shSidebarRight', right);
    const w = parseInt(P.layout('sidebarWidth', ''), 10);
    if (w && !this.sidebarHidden()) this.sidebar.style.width = Math.max(160, Math.min(600, w)) + 'px';
    if (window.bcodeKeys) window.bcodeKeys.rebuild();
    if (window.bcodeTabs) window.bcodeTabs.render();   // layout.tabstripWithTree
    this.renderToolbar();
    if (window.bcodeViewer && window.bcodeViewer.editor) window.bcodeViewer.editor.layout();
  }

  /// Ẩn / hiện nhanh cả thanh menu + toolbar + đường dẫn (phím tắt tuỳ gán), nhớ lại ở lần mở sau.
  toggleChrome() {
    const P = window.bcodePrefs;
    const on = !(P.layoutOn('menubar') || P.layoutOn('toolbar') || P.layoutOn('breadcrumb'));
    const layout = { ...P.data.layout };
    for (const k of ['menubar', 'toolbar', 'breadcrumb']) layout[k] = on ? '1' : '0';
    P.save({ layout });
    this.applyLayout();
  }

  applyAutoHide() {
    document.body.classList.toggle('shAutoHide', this.autoHide);
    if (!this.autoHide) { this.top.classList.remove('shPeek'); return; }
    if (this.peekWired) return;
    this.peekWired = true;
    document.addEventListener('mousemove', (e) => {
      if (!this.autoHide) return;
      if (e.clientY <= 4) this.top.classList.add('shPeek');
      else if (!this.openMenu && !this.top.contains(e.target) && e.clientY > this.top.offsetHeight + 8) this.top.classList.remove('shPeek');
    });
  }

  // ---- breadcrumb ----------------------------------------------------------------------------

  /// Đường dẫn ĐẦY ĐỦ như thanh "File Path" của FCodeViewer: nhãn "File Path" (bấm = copy cả đường dẫn), máy chủ UNC (\\172.168.5.14) tô màu
  /// riêng, rồi từng thư mục tới file (đậm). Mỗi đoạn mở thư mục đó trong Explorer, đoạn cuối mở thư mục chứa file và chọn sẵn file.
  /// Dài quá bề ngang thì thanh cuộn ngang (lăn chuột) và luôn cuộn sẵn về cuối để thấy tên file.
  renderBreadcrumb(path) {
    this.breadcrumb.textContent = '';
    if (!path) return;
    const isUnc = path.startsWith('\\\\');
    const parts = path.split('\\').filter(Boolean);
    const full = [];
    let acc = isUnc ? '\\\\' : '';
    for (const p of parts) { acc = acc.endsWith('\\') ? acc + p : acc + '\\' + p; full.push(acc); }
    const label = Object.assign(document.createElement('span'), { className: 'shCrumbLabel', textContent: 'File Path', title: 'Bấm để copy đường dẫn:\n' + path });
    label.onclick = () => {
      navigator.clipboard.writeText(path).then(() => this.bcode.showToast('Đã copy: ' + path, 2500), () => {});
    };
    this.breadcrumb.appendChild(label);
    const link = (text, target, title, cls) => {
      const a = document.createElement('span');
      a.className = 'shCrumb' + (cls ? ' ' + cls : '');
      a.textContent = text;
      a.title = title;
      a.onclick = () => this.cmd('tree.openFolder', target);
      this.breadcrumb.appendChild(a);
    };
    const sep = () => this.breadcrumb.appendChild(Object.assign(document.createElement('span'), { className: 'shCrumbSep', textContent: '›' }));
    for (let i = 0; i < parts.length; i++) {
      const isFile = i === parts.length - 1;
      link(i === 0 && isUnc ? '\\\\' + parts[0] : parts[i], full[i], full[i], isFile ? 'file' : (i === 0 ? 'root' : ''));
      if (!isFile) sep();
    }
    this.breadcrumb.scrollLeft = this.breadcrumb.scrollWidth;
    if (!this.breadcrumb._wheel) {
      this.breadcrumb._wheel = true;
      this.breadcrumb.addEventListener('wheel', (e) => { if (e.deltaY) { e.preventDefault(); this.breadcrumb.scrollLeft += e.deltaY; } }, { passive: false });
    }
  }

  // ---- sidebar tree --------------------------------------------------------------------------

  buildSidebar() {
    const width = parseInt(this.load('shell.sidebarWidth') || '260', 10);
    this.sidebar.style.width = Math.max(160, Math.min(600, width)) + 'px';
    if (this.load('shell.sidebarHidden') === '1') this.setSidebarHidden(true);
    this.sidebar.innerHTML = '<div class="shTreeHeader"></div><div class="shTree" tabindex="0"></div>';
    this.treeHeader = this.sidebar.querySelector('.shTreeHeader');
    this.treeEl = this.sidebar.querySelector('.shTree');
    this.selKeys = new Set();   // các file đang được chọn bằng Ctrl+bấm (để Ctrl+C copy đường dẫn nhiều file)
    if (!document.getElementById('shSelCss')) {
      const st = document.createElement('style'); st.id = 'shSelCss';
      st.textContent = '.shRow.file.sel { background: color-mix(in srgb, var(--bc-accent) 45%, var(--bc-bg)) !important; color: var(--bc-text) !important; box-shadow: inset 4px 0 0 var(--bc-accent), inset 0 0 0 1px var(--bc-accent) !important; font-weight: 600; }' +
        '.shRow.file.sel .shRowLabel::before { content: "\\2713  "; color: var(--bc-accent); font-weight: 700; }' +
        '.shTreeHeader.shSelInfo { color: var(--bc-accent-text, #fff); background: var(--bc-accent); }';
      document.head.appendChild(st);
    }
    this.treeEl.addEventListener('keydown', (e) => {
      if (e.key === 'Delete' && this.focusedKey) { e.preventDefault(); this.cmd('tree.remove', this.focusedKey); }
      // Ctrl+C: copy đường dẫn các file đã chọn (Ctrl+bấm); chưa chọn file nào thì copy dòng đang đứng (file / cả thư mục / cả dự án).
      else if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 'c') {
        const keys = this.selKeys.size ? this.selectedKeysInOrder() : (this.focusedKey ? [this.focusedKey] : []);
        if (keys.length) { e.preventDefault(); e.stopPropagation(); this.cmd('tree.copyMany', keys.join('\n')); }
      }
      else if (e.key === 'Escape' && this.selKeys.size) { e.preventDefault(); this.clearTreeSelection(); }
    });

    this.resizer.addEventListener('mousedown', (e) => {
      e.preventDefault();
      const startX = e.clientX, startW = this.sidebar.offsetWidth;
      const dir = document.getElementById('main').classList.contains('shSidebarRight') ? -1 : 1;
      const move = (ev) => { this.sidebar.style.width = Math.max(160, Math.min(600, startW + dir * (ev.clientX - startX))) + 'px'; };
      const up = () => {
        document.removeEventListener('mousemove', move);
        document.removeEventListener('mouseup', up);
        this.save('shell.sidebarWidth', String(this.sidebar.offsetWidth));
      };
      document.addEventListener('mousemove', move);
      document.addEventListener('mouseup', up);
    });
  }

  sidebarHidden() { return this.sidebar.style.display === 'none'; }
  setSidebarHidden(hidden) {
    this.sidebar.style.display = hidden ? 'none' : '';
    this.resizer.style.display = hidden ? 'none' : '';
  }
  toggleSidebar() {
    const hide = !this.sidebarHidden();
    this.setSidebarHidden(hide);
    this.save('shell.sidebarHidden', hide ? '1' : '0');
    // Hidden tree → the tab row lists every tree file instead (see tabs.js items()).
    if (window.bcodeTabs) window.bcodeTabs.render();
  }

  /// Khoá các file đang chọn, theo đúng thứ tự hiện trong cây (không theo thứ tự bấm).
  selectedKeysInOrder() {
    const out = [];
    const walk = (nodes) => { for (const n of nodes || []) { if (n.kind === 'file') { if (this.selKeys.has(n.key)) out.push(n.key); } else walk(n.children); } };
    walk((this.tree && this.tree.nodes) || []);
    return out;
  }
  clearTreeSelection() {
    this.selKeys.clear();
    this.treeEl.querySelectorAll('.shRow.sel').forEach((r) => r.classList.remove('sel'));
    this.paintTreeSelection();
  }
  /// Dòng đầu cây: đang chọn bao nhiêu file (để biết chắc Ctrl+bấm đã ăn) — không chọn gì thì hiện lại tiêu đề thường.
  paintTreeSelection() {
    const n = this.selKeys.size;
    this.treeHeader.textContent = n ? 'Đã chọn ' + n + ' file — Ctrl+C: copy đường dẫn · Esc: bỏ chọn' : ((this.tree && this.tree.header) || '');
    this.treeHeader.classList.toggle('shSelInfo', n > 0);
  }

  setTree(data) {
    this.tree = data;
    // File đã bị bỏ khỏi cây thì bỏ luôn khỏi vùng chọn.
    if (this.selKeys && this.selKeys.size) {
      const alive = new Set();
      const mark = (nodes) => { for (const n of nodes || []) { if (n.kind === 'file') alive.add(n.key); else mark(n.children); } };
      mark(data.nodes);
      [...this.selKeys].forEach((k) => { if (!alive.has(k)) this.selKeys.delete(k); });
    }
    this.paintTreeSelection();
    this.renderBreadcrumb(data.activePath);
    const scroll = this.treeEl.scrollTop;
    let activeRow = null;
    const active = (data.activePath || '').toLowerCase();
    const chain = this.activeChain(data.activePath);
    const open = this.openPathSet();
    // Dựng mọi dòng vào DocumentFragment (ngoài màn hình) rồi thay 1 lần — trình duyệt chỉ tính bố cục 1 lần thay vì sau từng dòng.
    const frag = document.createDocumentFragment();
    const walk = (nodes, depth, parent) => {
      for (const n of nodes) {
        const row = this.renderRow(n, depth, parent);
        if (parent) row.dataset.parent = parent.key;   // kéo thả file chỉ trong cùng thư mục (beginFileDrag)
        if (n.kind === 'file' && n.path && n.path.toLowerCase() === active) { row.classList.add('active'); activeRow = row; }
        if (n.kind === 'file' && n.path && open.has(n.path.toLowerCase())) row.classList.add('open');
        if (n.kind !== 'file' && chain.has(n.key)) row.classList.add('hasActive');
        frag.appendChild(row);
        if (n.kind !== 'file' && n.expanded) walk(n.children, depth + 1, n);
      }
    };
    walk(data.nodes || [], 0);
    this.treeEl.replaceChildren(frag);
    this.treeEl.scrollTop = scroll;
    if (data.reveal && activeRow) activeRow.scrollIntoView({ block: 'nearest' });
    // Cây ẩn: thanh tab liệt kê file của cây. Cây hiện: thanh tab chỉ giữ file không có trong cây (tabs.items) — cây đổi thì cả hai trường hợp đều phải vẽ lại.
    if (window.bcodeTabs) window.bcodeTabs.render();
  }

  /// Host đổi file đang mở mà cây không đổi (MainForm.PushShellActive): chuyển tô "active" sang dòng của file đó, cập nhật đường dẫn
  /// và cuộn cho thấy dòng — thay vì setTree vẽ lại toàn bộ cây mỗi lần Ctrl+Tab.
  setActive(path) {
    if (!this.tree) return;
    this.tree.activePath = path;
    this.renderBreadcrumb(path);
    const want = (path || '').toLowerCase();
    const open = this.openPathSet();
    let row = null;
    for (const r of this.treeEl.querySelectorAll('.shRow.file')) {
      const p = (r.dataset.path || '').toLowerCase();
      const hit = p === want;
      r.classList.toggle('active', hit);
      r.classList.toggle('open', open.has(p));
      if (hit) row = r;
    }
    const chain = this.activeChain(path);
    for (const r of this.treeEl.querySelectorAll('.shRow.group, .shRow.folder')) r.classList.toggle('hasActive', chain.has(r.dataset.key));
    if (row) row.scrollIntoView({ block: 'nearest' });
    // Cây ẩn thì thanh tab liệt kê cả file của cây — tab đang chọn do tabs.setActive (editor.activateDoc) lo, không cần vẽ lại cả thanh.
  }

  /// Khoá các dòng dự án / thư mục chứa file <paramref name="path"/> (để tô "đang chứa file active" — kể cả khi dự án đang thu gọn).
  activeChain(path) {
    const target = (path || '').toLowerCase(), out = new Set();
    if (!target) return out;
    const walk = (nodes, up) => {
      for (const n of nodes || []) {
        if (n.kind === 'file') { if (n.path && n.path.toLowerCase() === target) { up.forEach((k) => out.add(k)); return true; } }
        else if (walk(n.children, up.concat(n.key))) return true;
      }
      return false;
    };
    walk((this.tree && this.tree.nodes) || [], []);
    return out;
  }

  /// Đường dẫn (chữ thường) các file đang mở thành tab trong editor.
  openPathSet() {
    return new Set(((this.bcode && this.bcode.openPaths && this.bcode.openPaths()) || []).map((p) => p.toLowerCase()));
  }

  /// Các tab đang mở thuộc dự án / thư mục <paramref name="n"/> (theo file nằm trong nhánh đó của cây), theo thứ tự tab.
  openTabsOf(n) {
    const inNode = new Set();
    const walk = (nodes) => { for (const c of nodes || []) { if (c.kind === 'file') { if (c.path) inNode.add(c.path.toLowerCase()); } else walk(c.children); } };
    walk(n.children);
    return ((this.bcode && this.bcode.openPaths && this.bcode.openPaths()) || []).filter((p) => inNode.has(p.toLowerCase()));
  }

  /// Nút ✕ trên dòng dự án / dòng thư mục: đóng mọi tab của nhánh đó (closeProjectTabs) rồi bỏ mọi file của nhánh khỏi cây — như ✕ của dòng file, cho cả nhánh.
  /// Huỷ ở hộp hỏi file chưa lưu thì dừng, không bỏ file nào khỏi cây. Bỏ bằng "tree.forgetProject" (host chỉ xoá khỏi danh sách) chứ không
  /// "tree.remove": lúc này host có thể chưa nhận tin file đang mở đã đóng, khi đó tree.remove gọi closeActive() và đóng nhầm file của dự án khác.
  async closeProject(n) {
    const name = String(n.text || '').replace(/\s*\(\d+\)$/, '');
    const { closed, total } = await this.closeProjectTabs(n, true);
    if (closed < total) { this.bcode.showToast(`Đã đóng ${closed}/${total} tab của "${name}" — dừng ở file chưa lưu, chưa bỏ file nào khỏi danh sách.`, 3500); return; }
    const paths = [];
    const walk = (nodes) => { for (const c of nodes || []) { if (c.kind === 'file') { if (c.path) paths.push(c.path); } else walk(c.children); } };
    walk(n.children);
    const files = paths.length;
    // Dự án: bỏ theo tên dự án. Thư mục (Dir, Grid... — nút ✕ của dòng thư mục): bỏ đúng các file của thư mục đó.
    if (n.kind === 'group') this.cmd('tree.forgetProject', n.key); else this.cmd('tree.forgetFiles', paths.join('\n'));
    this.bcode.showToast(`Đã bỏ ${files} file của "${name}" khỏi danh sách` + (total ? ` (đóng ${total} tab).` : '.'), 3000);
  }

  /// Đóng mọi tab đang mở của dự án (file vẫn nằm trong cây, bấm lại là mở) — chuột phải dự án; nút ✕ dùng qua closeProject.
  /// File chưa lưu hỏi như đóng tab thường (editor.closeDoc); chọn "Huỷ" thì dừng, các tab còn lại giữ nguyên — như "Đóng tất cả" của thanh tab.
  /// Đóng file không sửa trước, file đang hiện sau cùng (đỡ nhảy qua lại giữa các file sắp đóng), file chưa lưu cuối cùng và được đưa lên
  /// màn hình trước khi hỏi để thấy đang hỏi file nào. Trả về { closed, total }; quiet = không hiện thông báo.
  async closeProjectTabs(n, quiet) {
    const name = String(n.text || '').replace(/\s*\(\d+\)$/, '');
    const paths = this.openTabsOf(n);
    if (!paths.length) { if (!quiet) this.bcode.showToast(`"${name}" không có tab nào đang mở.`, 2500); return { closed: 0, total: 0 }; }
    const dirty = (p) => { const d = this.bcode.docs.get(p); return !!(d && d.dirty); };
    const rank = (p) => (dirty(p) ? 2 : p === this.bcode.activePath ? 1 : 0);
    paths.sort((a, b) => rank(a) - rank(b));
    let closed = 0;
    for (const p of paths) {
      if (!this.bcode.docs.has(p)) { closed++; continue; }
      if (dirty(p) && this.bcode.activePath !== p) this.bcode.activateDoc(p);
      if (!(await this.bcode.closeDoc(p))) break;
      closed++;
    }
    if (!quiet) this.bcode.showToast(closed === paths.length ? `Đã đóng ${closed} tab của "${name}".`
      : `Đã đóng ${closed}/${paths.length} tab của "${name}" — dừng ở file chưa lưu.`, 3000);
    return { closed, total: paths.length };
  }

  /// Các file nằm CÙNG NHÁNH (cùng nhóm trực tiếp, vd "Grid" trong dự án KOG) với <paramref name="path"/>, theo thứ tự trong cây — để đóng 1 file
  /// thì chuyển sang file kế bên trong nhánh đó trước, hết file trong nhánh mới nhảy sang nhánh khác.
  branchFiles(path) {
    const target = (path || '').toLowerCase();
    let found = null;
    const walk = (nodes) => {
      for (const n of nodes || []) {
        if (found) return;
        if (n.kind === 'file') continue;
        const files = (n.children || []).filter((c) => c.kind === 'file' && c.path);
        if (files.some((f) => f.path.toLowerCase() === target)) { found = files.map((f) => f.path); return; }
        walk(n.children);
      }
    };
    walk((this.tree && this.tree.nodes) || []);
    return found || [];
  }

  /// <paramref name="parent"/> = nút cha (thư mục của file) — file lấy màu + chữ cái đầu của thư mục làm icon tròn (D = Dir, G = Grid...) và vạch dọc
  /// cùng màu bên trái, nhìn như cây của FCodeViewer: biết ngay file thuộc nhóm nào kể cả khi dòng thư mục đã cuộn khỏi màn hình.
  renderRow(n, depth, parent) {
    const row = document.createElement('div');
    row.className = 'shRow ' + n.kind + (n.dirty ? ' dirty' : '') + (n.kind === 'file' && this.selKeys && this.selKeys.has(n.key) ? ' sel' : '');
    row.style.paddingLeft = (6 + depth * 14) + 'px';
    if (n.path) { row.title = n.path; row.dataset.path = n.path; }   // setActive tìm dòng theo đường dẫn
    row.dataset.key = n.key;   // setActive tô dự án / thư mục chứa file đang mở (activeChain)
    if (n.kind === 'group') {
      row.appendChild(Object.assign(document.createElement('span'), { className: 'shTwisty', textContent: n.expanded ? '▾' : '▸' }));
    } else if (n.kind === 'folder') {
      const dot = document.createElement('span');
      dot.className = 'shDot';
      dot.style.background = n.color || 'var(--bc-text-muted)';
      row.appendChild(dot);
    } else if (n.kind === 'file' && parent && parent.kind === 'folder') {
      const color = parent.color || 'var(--bc-text-muted)';
      const guide = document.createElement('span');
      guide.className = 'shGuide';
      guide.style.left = (6 + (depth - 1) * 14 + 3) + 'px';
      guide.style.background = color;
      const icon = Object.assign(document.createElement('span'), { className: 'shFileIcon', textContent: (String(parent.text || '?').match(/[A-Za-z0-9]/) || ['?'])[0].toUpperCase() });
      icon.style.setProperty('--ic', color);
      row.append(guide, icon);
    }
    const label = document.createElement('span');
    label.className = 'shRowLabel';
    const count = n.kind === 'group' ? /^(.*?)(\s*\(\d+\))$/.exec(n.text) : null;
    if (count) {
      // "KOG (60)": tên dự án đậm, số file mờ hơn.
      label.textContent = count[1];
      label.appendChild(Object.assign(document.createElement('span'), { className: 'shCount', textContent: count[2] }));
    } else label.textContent = n.text + (n.dirty ? ' •' : '');
    if (n.kind === 'folder' && n.color) label.style.color = n.color;
    row.appendChild(label);

    if (n.kind === 'file') {
      const icons = document.createElement('span');
      icons.className = 'shRowIcons';
      const copy = Object.assign(document.createElement('span'), { className: 'shIcon', textContent: '⧉', title: 'Copy đường dẫn (file .f nếu có, không thì chính file này)' });
      copy.onclick = (e) => { e.stopPropagation(); this.cmd('tree.copy', n.key); };
      const close = Object.assign(document.createElement('span'), { className: 'shIcon', textContent: '✕', title: 'Bỏ khỏi danh sách (Delete)' });
      close.onclick = (e) => { e.stopPropagation(); this.cmd('tree.remove', n.key); };
      icons.append(copy, close);
      row.appendChild(icons);
    } else {
      const icons = document.createElement('span');
      icons.className = 'shRowIcons';
      const close = Object.assign(document.createElement('span'), { className: 'shIcon', textContent: '✕' });
      // Số tab tính lúc rê chuột (tab mở / đóng không vẽ lại dòng này).
      row.addEventListener('mouseenter', () => {
        const name = n.text.replace(/\s*\(\d+\)$/, ''), tabs = this.openTabsOf(n).length;
        close.title = n.kind === 'group'
          ? `Đóng dự án "${name}": đóng ${tabs} tab đang mở và bỏ mọi file của dự án khỏi danh sách (không xoá file trên đĩa)`
          : `Đóng thư mục "${name}": đóng ${tabs} tab đang mở và bỏ các file của thư mục này khỏi danh sách (không xoá file trên đĩa)`;
      });
      close.onclick = (e) => { e.stopPropagation(); this.closeProject(n); };
      icons.append(close);
      row.appendChild(icons);
    }

    row.addEventListener('mousedown', (e) => {
      this.focusedKey = n.key;
      // Ctrl+bấm vào file: chọn / bỏ chọn (không mở file) — rồi Ctrl+C để copy đường dẫn các file đã chọn.
      // Làm ngay ở mousedown: cây có thể bị vẽ lại giữa lúc nhấn và nhả chuột (host đẩy cây mới khi editor mất focus), khi đó sự kiện click không tới được dòng này nữa.
      if (n.kind === 'file' && e.button === 0 && (e.ctrlKey || e.metaKey)) {
        e.preventDefault();
        if (this.selKeys.has(n.key)) this.selKeys.delete(n.key); else this.selKeys.add(n.key);
        row.classList.toggle('sel', this.selKeys.has(n.key));
        this.treeEl.focus();
        this.paintTreeSelection();
      }
      else if (n.kind === 'file' && e.button === 0 && !e.shiftKey && !e.altKey && !e.target.closest('.shIcon')) this.beginFileDrag(e, n, row.dataset.parent);
    });
    row.addEventListener('click', (e) => {
      if (this._suppressClick) return;   // vừa kéo thả xong (beginFileDrag)
      if (n.kind === 'file' && (e.ctrlKey || e.metaKey)) return;   // đã xử lý ở mousedown
      if (this.selKeys.size) this.clearTreeSelection();
      if (n.kind === 'file') this.cmd('tree.open', n.path);
      else this.cmd('tree.toggle', n.key);
    });
    row.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      this.focusedKey = n.key;
      this.showTreeMenu(n, e.clientX, e.clientY);
    });
    return row;
  }

  /// Kéo thả 1 file lên / xuống trong CÙNG thư mục (Dir, Grid...) để sắp xếp. Tự làm bằng mousemove/mouseup gắn ở document, không dùng HTML5
  /// drag: host có thể đẩy cây mới (setTree dựng lại mọi dòng) giữa lúc đang kéo — dòng nguồn mất thì HTML5 drag tắt ngang, còn ở đây chỉ cần
  /// tìm lại dòng theo data-key. Nhích chuột dưới 5px vẫn là bấm thường (mở file). Thả → host lưu thứ tự (tree.reorder → RecentFilesStore.FileOrder).
  beginFileDrag(e, n, parentKey) {
    if (!parentKey) return;
    const startX = e.clientX, startY = e.clientY;
    let dragging = false, target = null, after = false;
    const clear = () => this.treeEl.querySelectorAll('.dropBefore, .dropAfter, .dragSrc').forEach((r) => r.classList.remove('dropBefore', 'dropAfter', 'dragSrc'));
    const move = (ev) => {
      if (!dragging) {
        if (Math.abs(ev.clientY - startY) < 5 && Math.abs(ev.clientX - startX) < 5) return;
        dragging = true;
        document.body.classList.add('shDragging');
        const sel = window.getSelection && window.getSelection(); if (sel) sel.removeAllRanges();
      }
      ev.preventDefault();
      clear();
      const src = [...this.treeEl.querySelectorAll('.shRow.file')].find((r) => r.dataset.key === n.key);
      if (src) src.classList.add('dragSrc');
      const box = this.treeEl.getBoundingClientRect();   // kéo sát mép trên / dưới thì tự cuộn
      if (ev.clientY < box.top + 24) this.treeEl.scrollTop -= 12; else if (ev.clientY > box.bottom - 24) this.treeEl.scrollTop += 12;
      const el = document.elementFromPoint(ev.clientX, ev.clientY);
      const row = el && el.closest ? el.closest('.shRow.file') : null;
      target = null;
      if (row && row.dataset.parent === parentKey && row.dataset.key !== n.key) {
        const r = row.getBoundingClientRect();
        after = ev.clientY > r.top + r.height / 2;
        target = row.dataset.key;
        row.classList.add(after ? 'dropAfter' : 'dropBefore');
      }
      document.body.classList.toggle('shNoDrop', !target);
    };
    const up = (ev) => {
      document.removeEventListener('mousemove', move, true);
      document.removeEventListener('mouseup', up, true);
      if (!dragging) return;
      move(ev);   // chỗ thả tính theo đúng vị trí nhả chuột (nhả ngoài cây / ngoài thư mục = không đổi gì)
      clear();
      document.body.classList.remove('shDragging', 'shNoDrop');
      this._suppressClick = true;
      setTimeout(() => { this._suppressClick = false; }, 0);
      if (target) this.reorderFile(parentKey, n.key, target, after);
    };
    document.addEventListener('mousemove', move, true);
    document.addEventListener('mouseup', up, true);
  }

  /// Đặt file <paramref name="srcKey"/> trước / sau <paramref name="targetKey"/> trong thư mục <paramref name="parentKey"/>: đổi ngay trên trang
  /// (dời dòng + mô hình cây) cho thấy liền, rồi gửi cả thứ tự mới của thư mục cho host lưu.
  reorderFile(parentKey, srcKey, targetKey, after) {
    let folder = null;
    const find = (nodes) => { for (const x of nodes || []) { if (folder) return; if (x.kind !== 'file') { if (x.key === parentKey) { folder = x; return; } find(x.children); } } };
    find(this.tree && this.tree.nodes);
    if (!folder) return;
    const kids = folder.children;
    const from = kids.findIndex((c) => c.key === srcKey);
    if (from < 0) return;
    const [moved] = kids.splice(from, 1);
    const to = kids.findIndex((c) => c.key === targetKey);
    if (to < 0) { kids.splice(from, 0, moved); return; }
    kids.splice(after ? to + 1 : to, 0, moved);
    const rows = [...this.treeEl.querySelectorAll('.shRow.file')];
    const srcRow = rows.find((r) => r.dataset.key === srcKey), tgtRow = rows.find((r) => r.dataset.key === targetKey);
    if (srcRow && tgtRow) tgtRow.parentNode.insertBefore(srcRow, after ? tgtRow.nextSibling : tgtRow);
    this.cmd('tree.reorder', kids.filter((c) => c.kind === 'file' && c.path).map((c) => c.path).join('\n'));
  }

  showTreeMenu(n, x, y) {
    const items = n.kind === 'file' ? [
      { label: 'Mở', run: () => this.cmd('tree.open', n.path) },
      { label: 'Mở thư mục chứa file', run: () => this.cmd('tree.openFolder', n.path) },
      { label: 'Copy đường dẫn (.f nếu có)', run: () => this.cmd('tree.copy', n.key) },
      ...(this.selKeys.size > 1 || (this.selKeys.size === 1 && !this.selKeys.has(n.key))
        ? [{ label: 'Copy đường dẫn ' + this.selKeys.size + ' file đã chọn', shortcut: 'Ctrl+C', run: () => this.cmd('tree.copyMany', this.selectedKeysInOrder().join('\n')) }] : []),
      { sep: true },
      { label: 'Bỏ khỏi danh sách', shortcut: 'Delete', run: () => this.cmd('tree.remove', n.key) },
    ] : n.kind === 'group' ? [
      { label: n.expanded ? 'Ẩn file của dự án này' : 'Hiện file của dự án này', run: () => this.cmd('tree.toggle', n.key) },
      { label: 'Ẩn file của TẤT CẢ dự án', run: () => this.cmd('tree.collapseAll') },
      { label: 'Hiện file của tất cả dự án', run: () => this.cmd('tree.expandAll') },
      { sep: true },
      { label: `Đóng mọi tab đang mở của dự án này (${this.openTabsOf(n).length})`, run: () => this.closeProjectTabs(n) },
      { label: 'Copy đường dẫn TẤT CẢ file của dự án này (.f nếu có)', run: () => this.cmd('tree.copyMany', n.key) },
      { label: `Bỏ tất cả file của "${n.text.replace(/\s*\(\d+\)$/, '')}" khỏi danh sách (như nút ✕)`, run: () => this.closeProject(n) },
    ] : [
      { label: n.expanded ? 'Ẩn file thư mục này' : 'Hiện file thư mục này', run: () => this.cmd('tree.toggle', n.key) },
      { label: 'Copy đường dẫn các file của thư mục này (.f nếu có)', run: () => this.cmd('tree.copyMany', n.key) },
      { sep: true },
      { label: `Đóng mọi tab đang mở của thư mục này (${this.openTabsOf(n).length})`, run: () => this.closeProjectTabs(n) },
      { label: `Bỏ các file của "${n.text}" khỏi danh sách (như nút ✕)`, run: () => this.closeProject(n) },
    ];
    this.closeMenu();
    const menu = this.renderMenu(items);
    document.body.appendChild(menu);
    menu.style.left = Math.min(x, window.innerWidth - menu.offsetWidth - 4) + 'px';
    menu.style.top = Math.min(y, window.innerHeight - menu.offsetHeight - 4) + 'px';
    const fake = document.createElement('span');
    this.openMenu = { index: -1, menu, titleEl: fake };
  }
}
