// Khung ứng dụng vẽ trong trang: thanh menu, thanh công cụ, đường dẫn file và cây file gần đây bên trái —
// thay cho MenuStrip / ToolStrip / breadcrumb / TreeView của WinForms để cả cửa sổ cùng một theme.
//
// Mô hình cây vẫn do MainForm giữ (RefreshProjectTree, trạng thái thu gọn lưu trong settings, đóng file đang
// mở trước khi xoá khỏi danh sách...): host đẩy bản JSON sang qua bcodeViewer.shellTree(), trang chỉ vẽ và gửi
// lệnh ngược lại bằng host.ShellCommand(cmd, arg). Lệnh của editor (Save, Undo, Problems...) gọi thẳng
// window.bcodeViewer như các phím tắt vẫn làm.

const SHELL_TOOLBAR = [
  { text: 'Save', run: (b) => b.saveActive(), title: 'Lưu (Ctrl+S)' },
  { text: 'Save As', run: (b) => b.saveActiveAs() },
  { sep: true },
  { text: 'Undo', run: (b) => b.undo(), title: 'Hoàn tác (Ctrl+Z)' },
  { text: 'Redo', run: (b) => b.redo(), title: 'Làm lại (Ctrl+Y)' },
  { sep: true },
  { text: 'Comment', run: (b) => b.toggleComment(), title: 'Comment/bỏ comment dòng (Ctrl+/)' },
  { sep: true },
  { text: 'Bookmark', run: (b) => b.toggleBookmark() },
  { text: '● Next', run: (b) => b.nextBookmark(), title: 'Tới bookmark kế tiếp' },
  { sep: true },
  { text: 'Refresh', run: (b) => b.refreshActive(), title: 'Tải lại file từ đĩa' },
  { sep: true },
  { text: 'Hint', run: (b) => b.openHintCode(), title: 'Hint Code — thư viện snippet' },
  { sep: true },
  { text: 'Tìm project', run: (b) => b.openSearch(), title: 'Tìm trong toàn bộ project (Ctrl+Shift+F)' },
  { text: 'Problems', run: (b) => b.toggleProblemsPanel(), title: 'Bảng lỗi/cảnh báo (Ctrl+Shift+M)' },
  { text: 'Outline', run: (b) => b.toggleOutlinePanel(), title: 'Cấu trúc file (Ctrl+Shift+U)' },
  { text: 'Xem Dir', run: (b) => b.toggleDirPreview(), title: 'Xem trước màn hình Dir (Alt+P)' },
  { text: 'Chạy SQL', run: (b) => b.runSql(), title: 'Chạy câu SQL tại con trỏ trên WS Bcode đang chọn (Ctrl+Enter)' },
  { text: 'Tách đôi', run: (b) => b.toggleSplit(), title: 'Tách đôi khung soạn thảo (Ctrl+\\)' },
  { text: 'Wrap', run: (b) => b.toggleWordWrap(), toggle: (b) => b.isWordWrap(), title: 'Tự ngắt dòng dài theo bề ngang khung (Alt+Z) — nhẹ hơn tự thu nhỏ cỡ chữ' },
  { sep: true },
  { text: '🤖 Claude Sidebar', host: 'toggleClaude' },
  { text: '✨ Gemini Sidebar', host: 'toggleGemini' },
  { text: '📎 Đính kèm file', host: 'attachFile', title: 'Đính kèm file đang mở vào ô chat của sidebar AI đang hiện' },
  { text: 'Clear Structure', host: 'clearStructure', title: 'Xoá file trong Controllers\\Structure\\App' },
  { text: 'Reset WebConfig', host: 'refreshWebConfig', title: 'Chạm web.config để IIS nạp lại site' },
];

class BcodeShell {
  constructor(bcode) {
    this.bcode = bcode;
    this.host = window.chrome && window.chrome.webview ? window.chrome.webview.hostObjects.host : null;
    this.tree = { header: '', nodes: [], activePath: null };
    this.hidden = new Set();
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
      if (this.openMenu && !e.target.closest('.shMenu') && !e.target.closest('.shMenuTitle')) this.closeMenu();
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
      this.hidden = new Set(this.state.hiddenToolbar || []);
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

    const toolbarItems = this.toolbarTokens().filter((t) => t !== '|').map((t) => ({
      label: this.tokenLabel(t), checked: !this.hidden.has(t), keepOpen: true,
      run: () => { this.hidden.has(t) ? this.hidden.delete(t) : this.hidden.add(t); this.saveHidden(); },
    }));
    toolbarItems.push({ sep: true }, { label: 'Hiện tất cả', keepOpen: true, run: () => { this.hidden.clear(); this.saveHidden(); } },
      { label: 'Sắp xếp / thêm nút...', run: () => this.bcode.openSettings('layout') });

    return [
      { title: 'File', items: [
        { label: 'New from Template...', shortcut: this.k('file.new'), run: () => b.openNewFromTemplate() },
        { sep: true },
        { label: 'Mở nhanh file...', shortcut: this.k('file.quickOpen'), run: () => window.bcodeQuickOpen && window.bcodeQuickOpen.open() },
        { label: 'Lịch sử file...', shortcut: this.k('file.history'), run: () => b.openHistory() },
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
        { label: 'Clear Structure App', run: () => this.cmd('clearStructure') },
        { label: 'Refresh Web.config', run: () => this.cmd('refreshWebConfig') },
      ] },
      { title: 'Help', items: [{ label: 'Giới thiệu...', run: () => this.showAbout() }] },
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
    this.toolbar.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      const title = [...this.menubar.children].find((t) => t.textContent === '⚙ Toolbar');
      if (title) this.showMenu(title, 5, e.clientX, e.clientY);
    });
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
    this.renderToolbar();
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
          sub.style.display = 'block';
          const rr = row.getBoundingClientRect();
          sub.style.left = (rr.right + sub.offsetWidth > window.innerWidth ? -sub.offsetWidth : rr.width) + 'px';
        });
        row.addEventListener('mouseleave', () => { sub.style.display = 'none'; });
      } else {
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

  saveHidden() {
    try { this.host.SetHiddenToolbar(JSON.stringify([...this.hidden])); } catch { /* no host */ }
    this.renderToolbar();
  }

  /// Thứ tự nút trên thanh công cụ: theo người dùng sắp xếp (Settings → Bố cục) hoặc mặc định; nút mới của bản cập nhật chưa có trong thứ tự thì nối vào cuối.
  /// Mỗi phần tử: tên nút (SHELL_TOOLBAR), "|" = vạch ngăn, "cmd:<id>" = nút thêm cho 1 chức năng bất kỳ của bảng phím tắt.
  defaultTokens() { return SHELL_TOOLBAR.map((t) => (t.sep ? '|' : t.text)); }

  toolbarTokens() {
    const known = new Set(SHELL_TOOLBAR.filter((t) => !t.sep).map((t) => t.text));
    const saved = (window.bcodePrefs && window.bcodePrefs.data.toolbarOrder) || [];
    if (!saved.length) return this.defaultTokens();
    const out = saved.filter((t) => t === '|' || known.has(t) || (t.startsWith('cmd:') && window.bcodeKeys && window.bcodeKeys.find(t.slice(4))));
    for (const t of known) if (!out.includes(t)) out.push(t);
    return out;
  }

  tokenLabel(t) {
    if (t.startsWith('cmd:')) { const c = window.bcodeKeys && window.bcodeKeys.find(t.slice(4)); return c ? c.label : t; }
    return t;
  }

  renderToolbar() {
    if (!this.toolbar) return;
    this.toolbar.textContent = '';
    let pendingSep = false, any = false;
    for (const tok of this.toolbarTokens()) {
      if (tok === '|') { pendingSep = any; continue; }
      if (this.hidden.has(tok)) continue;
      let text, title, click, toggleFn = null;
      if (tok.startsWith('cmd:')) {
        const c = window.bcodeKeys && window.bcodeKeys.find(tok.slice(4));
        if (!c) continue;
        const key = window.bcodeKeys.get(c.id);
        text = c.label; title = c.label + (key ? ` (${key})` : '');
        click = () => c.run(this.bcode);
      } else {
        const t = SHELL_TOOLBAR.find((x) => x.text === tok);
        if (!t) continue;
        text = t.text; title = t.title; toggleFn = t.toggle || null;
        click = () => (t.host ? this.cmd(t.host) : t.run(this.bcode));
      }
      if (pendingSep) { this.toolbar.appendChild(Object.assign(document.createElement('span'), { className: 'shTbSep' })); pendingSep = false; }
      const btn = document.createElement('button');
      btn.className = 'shTbBtn';
      if (toggleFn) btn.classList.toggle('on', !!toggleFn(this.bcode));   // nút bật/tắt (Wrap): sáng khi đang bật
      btn.textContent = text;
      if (title) btn.title = title;
      btn.addEventListener('mousedown', (e) => e.preventDefault()); // giữ focus trong editor
      btn.onclick = click;
      this.toolbar.appendChild(btn);
      any = true;
    }
  }

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
    // Cả khung trên cùng trống thì không để lại dải rỗng.
    if (this.top) this.top.style.display = (P.layoutOn('menubar') || P.layoutOn('toolbar') || P.layoutOn('breadcrumb')) ? '' : 'none';
    const right = P.layout('sidebarSide', 'left') === 'right';
    document.getElementById('main').classList.toggle('shSidebarRight', right);
    const w = parseInt(P.layout('sidebarWidth', ''), 10);
    if (w && !this.sidebarHidden()) this.sidebar.style.width = Math.max(160, Math.min(600, w)) + 'px';
    if (window.bcodeKeys) window.bcodeKeys.rebuild();
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

  /// Từ thư mục site (ngay trên App_Data) trở đi; phần trước gộp thành "…". Mỗi đoạn mở thư mục đó
  /// trong Explorer, đoạn cuối mở thư mục chứa file và chọn sẵn file — giống breadcrumb WinForms cũ.
  renderBreadcrumb(path) {
    this.breadcrumb.textContent = '';
    if (!path) return;
    const isUnc = path.startsWith('\\\\');
    const parts = path.split('\\').filter(Boolean);
    const full = [];
    let acc = isUnc ? '\\\\' : '';
    for (const p of parts) { acc = acc.endsWith('\\') ? acc + p : acc + '\\' + p; full.push(acc); }
    const appData = parts.findIndex((p) => p.toLowerCase() === 'app_data');
    const first = appData > 0 ? appData - 1 : Math.max(0, parts.length - 4);
    const link = (text, target, title, bold) => {
      const a = document.createElement('span');
      a.className = 'shCrumb' + (bold ? ' file' : '');
      a.textContent = text;
      a.title = title;
      a.onclick = () => this.cmd('tree.openFolder', target);
      this.breadcrumb.appendChild(a);
    };
    const sep = () => this.breadcrumb.appendChild(Object.assign(document.createElement('span'), { className: 'shCrumbSep', textContent: '›' }));
    if (first > 0) { link('…', full[first - 1], path, false); sep(); }
    for (let i = first; i < parts.length; i++) {
      const isFile = i === parts.length - 1;
      link(parts[i], full[i], full[i], isFile);
      if (!isFile) sep();
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
    this.treeEl.addEventListener('keydown', (e) => {
      if (e.key === 'Delete' && this.focusedKey) { e.preventDefault(); this.cmd('tree.remove', this.focusedKey); }
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

  setTree(data) {
    this.tree = data;
    this.treeHeader.textContent = data.header || '';
    this.renderBreadcrumb(data.activePath);
    const scroll = this.treeEl.scrollTop;
    this.treeEl.textContent = '';
    let activeRow = null;
    const active = (data.activePath || '').toLowerCase();
    const walk = (nodes, depth) => {
      for (const n of nodes) {
        const row = this.renderRow(n, depth);
        if (n.kind === 'file' && n.path && n.path.toLowerCase() === active) { row.classList.add('active'); activeRow = row; }
        this.treeEl.appendChild(row);
        if (n.kind !== 'file' && n.expanded) walk(n.children, depth + 1);
      }
    };
    walk(data.nodes || [], 0);
    this.treeEl.scrollTop = scroll;
    if (data.reveal && activeRow) activeRow.scrollIntoView({ block: 'nearest' });
    if (window.bcodeTabs && this.sidebarHidden()) window.bcodeTabs.render();
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

  renderRow(n, depth) {
    const row = document.createElement('div');
    row.className = 'shRow ' + n.kind + (n.dirty ? ' dirty' : '');
    row.style.paddingLeft = (6 + depth * 14) + 'px';
    if (n.path) row.title = n.path;
    if (n.kind === 'group') {
      row.appendChild(Object.assign(document.createElement('span'), { className: 'shTwisty', textContent: n.expanded ? '▾' : '▸' }));
    } else if (n.kind === 'folder') {
      const dot = document.createElement('span');
      dot.className = 'shDot';
      dot.style.background = n.color || 'var(--bc-text-muted)';
      row.appendChild(dot);
    }
    const label = document.createElement('span');
    label.className = 'shRowLabel';
    label.textContent = n.text + (n.dirty ? ' •' : '');
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
    }

    row.addEventListener('mousedown', () => { this.focusedKey = n.key; });
    row.addEventListener('click', () => {
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

  showTreeMenu(n, x, y) {
    const items = n.kind === 'file' ? [
      { label: 'Mở', run: () => this.cmd('tree.open', n.path) },
      { label: 'Mở thư mục chứa file', run: () => this.cmd('tree.openFolder', n.path) },
      { label: 'Copy đường dẫn (.f nếu có)', run: () => this.cmd('tree.copy', n.key) },
      { sep: true },
      { label: 'Bỏ khỏi danh sách', shortcut: 'Delete', run: () => this.cmd('tree.remove', n.key) },
    ] : n.kind === 'group' ? [
      { label: n.expanded ? 'Ẩn file của dự án này' : 'Hiện file của dự án này', run: () => this.cmd('tree.toggle', n.key) },
      { label: 'Ẩn file của TẤT CẢ dự án', run: () => this.cmd('tree.collapseAll') },
      { label: 'Hiện file của tất cả dự án', run: () => this.cmd('tree.expandAll') },
      { sep: true },
      { label: `Bỏ tất cả file của "${n.text.replace(/\s*\(\d+\)$/, '')}" khỏi danh sách`, run: () => this.cmd('tree.remove', n.key) },
    ] : [
      { label: n.expanded ? 'Ẩn file thư mục này' : 'Hiện file thư mục này', run: () => this.cmd('tree.toggle', n.key) },
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
