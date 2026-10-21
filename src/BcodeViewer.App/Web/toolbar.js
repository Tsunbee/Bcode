// Thanh công cụ tuỳ biến của BcodeViewer — cùng cách dùng với Quick Access + nhóm công cụ (Alt+1…5) của Bcode, làm lại trong trang.
//
// Cấu hình nằm trong bcodePrefs (keys.js → EditorBridge.GetUiPrefs/SetUiPrefs → ViewerSettings, %AppData%\Bcode\viewer-settings.json):
//   toolbarOrder  — token trên thanh theo thứ tự: tên nút (SHELL_TOOLBAR ở shell.js), "|" = vạch ngăn, "cmd:<id>" = nút cho 1 chức năng
//                   của bảng phím tắt, "grp:<id>" = 1 nhóm. Rỗng và không có nhóm = thanh mặc định, y như trước khi có tuỳ biến.
//   toolbarGroups — [{id, name, items:[token]}]: nhóm hiện thành nút "Tên ▾"; bấm hoặc Alt+N (bar.groupN ở keys.js, đổi được ở Settings → Phím tắt)
//                   thả menu các nút trong nhóm. Nút đã vào nhóm thì không đứng riêng trên thanh.
//   hiddenToolbar — token đã BỎ GHIM: không hiện trên thanh / trong nhóm, vẫn chạy được ở ⚙ Toolbar → "Nút chưa ghim", menu chính và phím tắt.
//   layout.toolbarPos   — "top" (mặc định, dưới menu) | "menu" (cùng hàng với menu) | "bottom" (đáy cửa sổ).
//   layout.toolbarStyle — "text" (mặc định, chữ như cũ) | "both" (icon + chữ) | "icon" (chỉ icon, chữ ở tooltip).
//
// Trên thanh: kéo nút để đổi chỗ, thả vào giữa nút nhóm = cho vào nhóm; chuột phải nút = Bỏ ghim / Chuyển vào nhóm / vạch ngăn / kiểu nút / vị trí.
// Settings → Bố cục có danh sách đầy đủ (kéo thả, tick ghim, nhóm, thêm nút) dùng chung BcodeTbModel ở đây.

const BcodeTbModel = {
  defaults() { return SHELL_TOOLBAR.map((t) => (t.sep ? '|' : t.text)); },

  valid(tok) {
    if (typeof tok !== 'string' || !tok) return false;
    if (tok.startsWith('cmd:')) return !!(window.bcodeKeys && window.bcodeKeys.find(tok.slice(4)));
    return SHELL_TOOLBAR.some((t) => !t.sep && t.text === tok);
  },

  /// Chuẩn hoá cấu hình đã lưu → {order, groups} mới (không dùng chung mảng với bản lưu): bỏ token lạ / trùng, nhóm chưa có trên thanh thì
  /// nối vào cuối, nút mới của bản cập nhật chèn ngay sau nút đứng trước nó trong thứ tự mặc định (kể cả khi nút đó đang nằm trong nhóm).
  normalize(savedOrder, savedGroups) {
    const used = new Set();
    const groups = [];
    for (const g of savedGroups || []) {
      if (!g || !g.id || groups.some((x) => x.id === String(g.id))) continue;
      const items = [];
      for (const t of g.items || []) if (this.valid(t) && !used.has(t)) { used.add(t); items.push(t); }
      groups.push({ id: String(g.id), name: String(g.name || 'Nhóm'), items });
    }
    const order = [];
    const seenGroup = new Set();
    for (const t of (savedOrder && savedOrder.length ? savedOrder : this.defaults())) {
      if (t === '|') { order.push(t); continue; }
      if (typeof t === 'string' && t.startsWith('grp:')) {
        const id = t.slice(4);
        if (groups.some((g) => g.id === id) && !seenGroup.has(id)) { seenGroup.add(id); order.push(t); }
        continue;
      }
      if (this.valid(t) && !used.has(t)) { used.add(t); order.push(t); }
    }
    for (const g of groups) if (!seenGroup.has(g.id)) order.push('grp:' + g.id);
    const defs = this.defaults().filter((t) => t !== '|');
    defs.forEach((t, i) => {
      if (used.has(t)) return;
      used.add(t);
      for (let j = i - 1; j >= 0; j--) {
        const at = order.indexOf(defs[j]);
        if (at >= 0) { order.splice(at + 1, 0, t); return; }
        const g = groups.find((x) => x.items.includes(defs[j]));
        if (g) { g.items.splice(g.items.indexOf(defs[j]) + 1, 0, t); return; }
      }
      order.push(t);
    });
    return { order, groups };
  },

  isDefault(m) { return !m.groups.length && JSON.stringify(m.order) === JSON.stringify(this.defaults()); },

  /// "Gợi ý nhóm" (như nút cùng tên ở Quick Access của Bcode): ghim các nút dùng nhiều nhất, phần còn lại chia nhóm theo vùng chức năng.
  /// Nút thêm (cmd:) của người dùng giữ lại ở cuối thanh; nút đang bỏ ghim vẫn bỏ ghim.
  SUGGESTED: {
    pinned: ['Save', '⚠ Check', '|', 'Undo', 'Redo', '|', 'Xem Dir', 'Chạy SQL', 'Wrap'],
    groups: [
      ['📄 Tệp', ['Save As', 'Refresh', 'Hint']],
      ['✎ Soạn', ['Comment', 'Bookmark', '● Next', 'Tách đôi']],
      ['🔍 Xem', ['Tìm project', 'Problems', 'Outline']],
      ['🤖 AI', ['🤖 Claude Sidebar', '✨ Gemini Sidebar', '📎 Đính kèm file']],
      ['🌐 Site', ['Clear Structure', 'Reset WebConfig', 'Open WebConfig']],
    ],
  },

  suggest(m) {
    const extras = [...m.order, ...m.groups.flatMap((g) => g.items)].filter((t) => t.startsWith('cmd:'));
    const groups = this.SUGGESTED.groups.map(([name, items], i) => ({ id: 'g' + (i + 1), name, items: items.filter((t) => this.valid(t)) }))
      .filter((g) => g.items.length);
    const order = [...this.SUGGESTED.pinned, '|', ...groups.map((g) => 'grp:' + g.id), ...(extras.length ? ['|', ...extras] : [])];
    return this.normalize(order, groups);   // nút mới của bản cập nhật chưa có trong gợi ý: tự chèn cạnh nút đứng trước nó
  },

  newGroupId(m) {
    let n = 1;
    while (m.groups.some((g) => g.id === 'g' + n)) n++;
    return 'g' + n;
  },

  /// Gỡ token (nút / nhóm) khỏi chỗ đang đứng — trên thanh hoặc trong nhóm.
  detach(m, tok) {
    const at = m.order.indexOf(tok);
    if (at >= 0) m.order.splice(at, 1);
    for (const g of m.groups) { const j = g.items.indexOf(tok); if (j >= 0) g.items.splice(j, 1); }
  },

  /// Bỏ nhóm, đưa các nút của nó ra thanh đúng chỗ nhóm đứng.
  ungroup(m, id) {
    const g = m.groups.find((x) => x.id === id);
    if (!g) return [];
    const at = m.order.indexOf('grp:' + id);
    m.order.splice(at < 0 ? m.order.length : at, at < 0 ? 0 : 1, ...g.items);
    m.groups.splice(m.groups.indexOf(g), 1);
    return g.items;
  },

  /// Nhãn để liệt kê token trong menu / danh sách cấu hình.
  label(tok, m) {
    if (tok.startsWith('grp:')) { const g = m && m.groups.find((x) => x.id === tok.slice(4)); return g ? g.name + ' ▾' : tok; }
    if (tok.startsWith('cmd:')) { const c = window.bcodeKeys && window.bcodeKeys.find(tok.slice(4)); return c ? c.label : tok; }
    return tok;
  },
};

/// "⚠ Check" → "Check", "🤖 Claude Sidebar" → "Claude Sidebar": bỏ ký hiệu đứng đầu tên nút (đã có icon riêng ở kiểu icon).
function bcodeTbPlain(text) { return String(text).replace(/^[^\p{L}\p{N}]+/u, '').trim() || String(text); }

class BcodeToolbar {
  constructor(shell) {
    this.shell = shell;
    this.el = shell.toolbar;
    this.groupsOnBar = [];   // id các nhóm đang hiện trên thanh, trái → phải: Alt+N mở nhóm thứ N
    this.injectCss();
    this.el.addEventListener('contextmenu', (e) => this.onContextMenu(e));
    this.el.addEventListener('mousedown', (e) => this.onMouseDown(e));
    // Vừa kéo thả xong thì nút dưới chuột không được tính là bấm.
    this.el.addEventListener('click', (e) => { if (this.justDragged) { e.preventDefault(); e.stopPropagation(); } }, true);
  }

  get bcode() { return this.shell.bcode; }
  get prefs() { return window.bcodePrefs; }

  model() { const d = this.prefs.data; return BcodeTbModel.normalize(d.toolbarOrder, d.toolbarGroups); }
  hidden() { return new Set(this.prefs.data.hiddenToolbar || []); }
  pos() { const p = this.prefs.layout('toolbarPos', 'top'); return p === 'menu' || p === 'bottom' ? p : 'top'; }
  style() { const s = this.prefs.layout('toolbarStyle', 'text'); return s === 'both' || s === 'icon' ? s : 'text'; }

  /// Lưu cấu hình (ghi ngay vào viewer-settings.json) rồi vẽ lại. Trùng mặc định thì lưu thứ tự rỗng để nút mới của bản cập nhật tự hiện đúng chỗ.
  commit(m, hidden) {
    const p = this.prefs.save({
      toolbarOrder: BcodeTbModel.isDefault(m) ? [] : m.order,
      toolbarGroups: m.groups,
      hiddenToolbar: [...(hidden || this.hidden())],
    });
    this.render();
    return p;
  }

  setLayout(key, value) {
    const layout = { ...this.prefs.data.layout };
    if (value == null) delete layout[key]; else layout[key] = value;
    this.prefs.save({ layout });
    this.shell.applyLayout();
  }

  async resetDefaults() {
    if (!(await window.bcodeUi.confirm('Đưa thanh công cụ về như mặc định (thứ tự, nút đã bỏ ghim, nhóm, vị trí, kiểu nút)?', { title: 'Khôi phục mặc định' }))) return;
    const layout = { ...this.prefs.data.layout };
    delete layout.toolbarPos;
    delete layout.toolbarStyle;
    await this.prefs.save({ toolbarOrder: [], toolbarGroups: [], hiddenToolbar: [], layout });
    this.shell.applyLayout();
  }

  /// Nút ứng với 1 token: chữ, icon, tooltip, phím tắt (theo bảng phím hiện hành), việc khi bấm.
  info(tok) {
    if (tok.startsWith('cmd:')) {
      const c = window.bcodeKeys && window.bcodeKeys.find(tok.slice(4));
      if (!c) return null;
      const key = window.bcodeKeys.get(c.id);
      return { text: c.label, plain: c.label, icon: null, title: c.label + (key ? ` (${key})` : ''), cmd: c.id, run: () => c.run(this.bcode) };
    }
    const t = SHELL_TOOLBAR.find((x) => !x.sep && x.text === tok);
    if (!t) return null;
    return { text: t.text, plain: bcodeTbPlain(t.text), icon: t.icon || null, title: t.title || '', cmd: t.cmd || '', toggle: t.toggle || null,
      run: () => (t.host ? this.shell.cmd(t.host) : t.run(this.bcode)) };
  }

  keyOf(it) { return it && it.cmd && window.bcodeKeys ? window.bcodeKeys.get(it.cmd) : ''; }

  // ---- vẽ thanh ------------------------------------------------------------------------------

  render() {
    const el = this.el;
    if (!el) return;
    el.textContent = '';
    const style = this.style();
    el.classList.toggle('shTbIcon', style === 'icon');
    el.classList.toggle('shTbBoth', style === 'both');
    const m = this.model();
    const hidden = this.hidden();
    this.groupsOnBar = [];
    let pendingSep = -1, any = false;
    m.order.forEach((tok, idx) => {
      if (tok === '|') { if (any) pendingSep = idx; return; }
      if (hidden.has(tok)) return;
      let btn;
      if (tok.startsWith('grp:')) {
        const g = m.groups.find((x) => x.id === tok.slice(4));
        if (!g) return;
        btn = this.groupButton(g, this.groupsOnBar.length, hidden);
        this.groupsOnBar.push(g.id);
      } else {
        const it = this.info(tok);
        if (!it) return;
        btn = this.itemButton(it, style);
      }
      if (pendingSep >= 0) {
        const sep = Object.assign(document.createElement('span'), { className: 'shTbSep' });
        sep.dataset.idx = pendingSep;
        el.appendChild(sep);
        pendingSep = -1;
      }
      btn.dataset.idx = idx;
      btn.dataset.tok = tok;
      el.appendChild(btn);
      any = true;
    });
  }

  itemButton(it, style) {
    const btn = document.createElement('button');
    btn.className = 'shTbBtn';
    if (it.toggle) btn.classList.toggle('on', !!it.toggle(this.bcode));   // nút bật/tắt (Wrap): sáng khi đang bật
    if (style === 'text') btn.textContent = it.text;
    else {
      btn.appendChild(this.iconEl(it));
      if (style === 'both') btn.appendChild(document.createTextNode(it.plain));
    }
    let title = it.title;
    if (style !== 'text' && !title.startsWith(it.plain)) { const key = this.keyOf(it); title = it.plain + (title ? ' — ' + title : (key ? ` (${key})` : '')); }
    if (title) btn.title = title;
    btn.onclick = () => it.run();
    return btn;
  }

  iconEl(it) {
    if (it.icon) return Object.assign(document.createElement('span'), { className: 'shTbIc', textContent: it.icon });
    // Nút không có icon riêng (nút thêm cho 1 chức năng): 2 chữ đầu của tên.
    const ab = it.plain.split(/\s+/).filter((w) => /^\p{L}/u.test(w)).slice(0, 2).map((w) => w[0].toUpperCase()).join('') || it.plain.slice(0, 2);
    return Object.assign(document.createElement('span'), { className: 'shTbIc shTbAbbr', textContent: ab });
  }

  groupButton(g, n, hidden) {
    const btn = document.createElement('button');
    btn.className = 'shTbBtn shTbGroup';
    // Kiểu "chỉ icon": tên nhóm bắt đầu bằng ký hiệu (vd "🛠 Công cụ") thì chỉ hiện ký hiệu đó.
    const lead = /^[^\p{L}\p{N}\s]+/u.exec(g.name);
    btn.textContent = this.style() === 'icon' && lead ? lead[0] : g.name;
    btn.appendChild(Object.assign(document.createElement('span'), { className: 'shTbCaret', textContent: '▾' }));
    const key = window.bcodeKeys ? window.bcodeKeys.get('bar.group' + (n + 1)) : '';
    const labels = g.items.filter((t) => !hidden.has(t)).map((t) => { const it = this.info(t); return it ? it.plain : t; });
    btn.title = g.name + (key ? ` (${key})` : '') + '\n' + (labels.length ? labels.join(', ') : '(nhóm trống)');
    btn.onclick = () => {
      const open = this.shell.openMenu;
      if (open && open.titleEl === btn) { this.shell.closeMenu(); return; }
      this.showGroup(g.id, btn);
    };
    return btn;
  }

  // ---- nhóm: menu thả xuống ------------------------------------------------------------------

  /// Alt+N: mở nhóm thứ N (tính từ trái, chỉ nhóm đang hiện trên thanh).
  openGroup(n) {
    const id = this.groupsOnBar[n];
    if (!id) {
      if (this.bcode && this.bcode.showToast) this.bcode.showToast(`Chưa có nhóm thứ ${n + 1} trên thanh công cụ — chuột phải 1 nút → Chuyển vào nhóm → Nhóm mới.`, 3500);
      return;
    }
    const btn = [...this.el.querySelectorAll('.shTbGroup')].find((b) => b.dataset.tok === 'grp:' + id);
    if (this.shell.openMenu && btn && this.shell.openMenu.titleEl === btn) { this.shell.closeMenu(); return; }
    this.showGroup(id, btn && btn.offsetParent ? btn : null);
  }

  showGroup(id, btn) {
    const m = this.model();
    const hidden = this.hidden();
    const g = m.groups.find((x) => x.id === id);
    if (!g) return;
    const items = [];
    g.items.filter((t) => !hidden.has(t)).forEach((tok) => {
      const it = this.info(tok);
      if (!it) return;
      items.push({ label: it.plain, shortcut: this.keyOf(it), checked: it.toggle ? !!it.toggle(this.bcode) : false,
        run: () => it.run(), context: (e) => this.groupItemMenu(id, tok, e) });
    });
    if (!items.length) items.push({ header: 'Nhóm trống — chuột phải 1 nút trên thanh → Chuyển vào nhóm' });
    const r = btn ? btn.getBoundingClientRect() : { left: window.innerWidth / 2 - 110, top: 40, bottom: 40 };
    const menu = this.shell.showMenuAt(items, r.left, r.bottom, btn);
    if (btn && this.pos() === 'bottom') menu.style.top = Math.max(0, r.top - menu.offsetHeight) + 'px';
    this.keyboardNav(menu);
  }

  /// Menu nhóm mở bằng phím: ↑/↓ chọn, Enter chạy, 1–9 chạy nút thứ N, Esc đóng (shell.onKey).
  keyboardNav(menu) {
    const rows = [...menu.querySelectorAll(':scope > .shItem')];
    if (!rows.length) return;
    let at = -1;
    const mark = (i) => { rows.forEach((r, j) => r.classList.toggle('kb', j === i)); at = i; };
    const onKey = (e) => {
      if (!document.body.contains(menu)) { document.removeEventListener('keydown', onKey, true); return; }
      if (e.ctrlKey || e.altKey || e.metaKey) return;
      let handled = true;
      if (e.key === 'ArrowDown') mark((at + 1) % rows.length);
      else if (e.key === 'ArrowUp') mark((at - 1 + rows.length) % rows.length);
      else if (e.key === 'Enter' && at >= 0) rows[at].click();
      else if (/^[1-9]$/.test(e.key) && rows[+e.key - 1]) rows[+e.key - 1].click();
      else handled = false;
      if (handled) { e.preventDefault(); e.stopImmediatePropagation(); }
    };
    document.addEventListener('keydown', onKey, true);
  }

  groupItemMenu(id, tok, e) {
    const m = this.model();
    const g = m.groups.find((x) => x.id === id);
    if (!g) return;
    const j = g.items.indexOf(tok);
    const move = (fn) => () => { fn(); this.commit(m); };
    const others = m.groups.filter((x) => x.id !== id);
    this.shell.showMenuAt([
      { header: BcodeTbModel.label(tok, m) },
      { label: 'Đưa ra thanh (ghim riêng)', run: move(() => { g.items.splice(j, 1); m.order.splice(m.order.indexOf('grp:' + id) + 1, 0, tok); }) },
      ...(others.length ? [{ label: 'Chuyển sang nhóm', submenu: others.map((o) => ({ label: o.name, run: move(() => { g.items.splice(j, 1); o.items.push(tok); }) })) }] : []),
      { label: 'Lên trên', run: move(() => { if (j > 0) g.items.splice(j - 1, 0, ...g.items.splice(j, 1)); }) },
      { label: 'Xuống dưới', run: move(() => { if (j < g.items.length - 1) g.items.splice(j + 1, 0, ...g.items.splice(j, 1)); }) },
      { sep: true },
      { label: 'Bỏ ghim (ẩn — vẫn chạy được qua menu / phím tắt)', run: () => { const h = this.hidden(); h.add(tok); this.commit(m, h); } },
    ], e.clientX, e.clientY);
  }

  // ---- chuột phải trên thanh -----------------------------------------------------------------

  onContextMenu(e) {
    e.preventDefault();
    const src = e.target.closest('[data-idx]');
    if (!src || !this.el.contains(src)) { this.shell.showToolbarMenu(e.clientX, e.clientY); return; }
    const m = this.model();
    const idx = +src.dataset.idx;
    const tok = m.order[idx];
    const items = [];
    const addSepLeft = { label: 'Thêm vạch ngăn bên trái', run: () => { m.order.splice(idx, 0, '|'); this.commit(m); } };
    if (tok === '|') {
      items.push({ label: 'Xoá vạch ngăn', run: () => { m.order.splice(idx, 1); this.commit(m); } });
    } else if (tok.startsWith('grp:')) {
      const id = tok.slice(4);
      const g = m.groups.find((x) => x.id === id);
      items.push(
        { header: 'Nhóm: ' + g.name },
        { label: 'Mở nhóm', run: () => this.showGroup(id, src) },
        { label: 'Đổi tên nhóm...', run: () => this.renameGroup(id) },
        { label: 'Tách nhóm (đưa các nút ra thanh)', run: () => { BcodeTbModel.ungroup(m, id); this.commit(m); } },
        { label: 'Xoá nhóm (bỏ ghim các nút trong nhóm)', run: () => {
          const h = this.hidden();
          BcodeTbModel.ungroup(m, id).forEach((t) => h.add(t));
          this.commit(m, h);
        } },
        addSepLeft,
      );
    } else {
      const isExtra = tok.startsWith('cmd:');
      items.push(
        { header: BcodeTbModel.label(tok, m) },
        isExtra
          ? { label: 'Bỏ khỏi thanh (nút thêm)', run: () => { m.order.splice(idx, 1); this.commit(m); } }
          : { label: 'Bỏ ghim (ẩn — vẫn chạy được qua menu / phím tắt)', run: () => { const h = this.hidden(); h.add(tok); this.commit(m, h); } },
        { label: 'Chuyển vào nhóm', submenu: [
          ...m.groups.map((g) => ({ label: g.name, run: () => { m.order.splice(idx, 1); g.items.push(tok); this.commit(m); } })),
          ...(m.groups.length ? [{ sep: true }] : []),
          { label: 'Nhóm mới...', run: () => this.newGroup(tok) },
        ] },
        addSepLeft,
      );
    }
    items.push({ sep: true }, ...this.layoutItems());
    this.shell.showMenuAt(items, e.clientX, e.clientY);
  }

  /// Kiểu nút / vị trí thanh / tuỳ chỉnh / mặc định — dùng ở menu chuột phải và menu ⚙ Toolbar.
  layoutItems() {
    const style = this.style(), pos = this.pos();
    return [
      { label: 'Kiểu nút', submenu: [['text', 'Chữ (mặc định)'], ['both', 'Icon + chữ'], ['icon', 'Chỉ icon']].map(([v, t]) => ({
        label: t, checked: style === v, run: () => this.setLayout('toolbarStyle', v === 'text' ? null : v) })) },
      { label: 'Vị trí thanh', submenu: [['top', 'Trên — dưới thanh menu (mặc định)'], ['menu', 'Cùng hàng với thanh menu'], ['bottom', 'Dưới cùng cửa sổ']].map(([v, t]) => ({
        label: t, checked: pos === v, run: () => this.setLayout('toolbarPos', v === 'top' ? null : v) })) },
      { label: 'Nhóm mới...', run: () => this.newGroup(null) },
      { label: 'Gợi ý nhóm (chia sẵn theo chức năng)...', run: () => this.applySuggested() },
      { label: 'Tuỳ chỉnh thanh công cụ (ghim, nhóm, thứ tự)...', run: () => this.bcode.openSettings('layout') },
      { label: 'Khôi phục mặc định', run: () => this.resetDefaults() },
    ];
  }

  async applySuggested() {
    const names = BcodeTbModel.SUGGESTED.groups.map(([n]) => n).join(', ');
    if (!(await window.bcodeUi.confirm(`Ghim Save, Check, Undo, Redo, Xem Dir, Chạy SQL, Wrap; các nút còn lại chia vào nhóm ${names} (Alt+1…5 mở nhóm). Thay cho thứ tự / nhóm đang có?\n\nKhông vừa ý thì "Khôi phục mặc định" để về như cũ.`, { title: 'Gợi ý nhóm' }))) return;
    this.commit(BcodeTbModel.suggest(this.model()));
  }

  /// Tạo nhóm mới; có <tok> thì nhóm đứng đúng chỗ nút đó và nhận luôn nút đó.
  async newGroup(tok) {
    const m0 = this.model();
    const name = await window.bcodeUi.prompt('Tên nhóm (có thể bắt đầu bằng 1 ký hiệu, vd "🛠 Công cụ" — kiểu "Chỉ icon" sẽ chỉ hiện ký hiệu):', 'Nhóm ' + (m0.groups.length + 1), { title: 'Nhóm mới' });
    if (!name || !name.trim()) return;
    const m = this.model();
    const id = BcodeTbModel.newGroupId(m);
    const g = { id, name: name.trim(), items: [] };
    m.groups.push(g);
    const at = tok ? m.order.indexOf(tok) : -1;
    if (at >= 0) { m.order.splice(at, 1, 'grp:' + id); g.items.push(tok); } else m.order.push('grp:' + id);
    this.commit(m);
  }

  async renameGroup(id) {
    const g0 = this.model().groups.find((x) => x.id === id);
    if (!g0) return;
    const name = await window.bcodeUi.prompt('Tên nhóm:', g0.name, { title: 'Đổi tên nhóm' });
    if (!name || !name.trim()) return;
    const m = this.model();
    m.groups.find((x) => x.id === id).name = name.trim();
    this.commit(m);
  }

  /// Mục cho menu ⚙ Toolbar: tick = ghim (hiện trên thanh), "Nút chưa ghim" chạy được ngay, rồi các tuỳ chọn bố cục.
  gearItems() {
    const m = this.model();
    const toggle = (tok) => { const h = this.hidden(); h.has(tok) ? h.delete(tok) : h.add(tok); this.commit(this.model(), h); };
    const hidden = this.hidden();
    const items = [];
    for (const tok of m.order) {
      if (tok === '|') continue;
      items.push({ label: BcodeTbModel.label(tok, m), checked: !hidden.has(tok), keepOpen: true, run: () => toggle(tok) });
      if (tok.startsWith('grp:')) {
        const g = m.groups.find((x) => x.id === tok.slice(4));
        for (const t of g.items) items.push({ label: '      ' + BcodeTbModel.label(t, m), checked: !hidden.has(t), keepOpen: true, run: () => toggle(t) });
      }
    }
    items.push({ sep: true }, { label: 'Hiện tất cả', keepOpen: true, run: () => this.commit(this.model(), new Set()) });
    const unpinned = [...hidden].filter((t) => !t.startsWith('grp:')).map((t) => [t, this.info(t)]).filter(([, it]) => it);
    if (unpinned.length) items.push({ label: 'Nút chưa ghim', submenu: unpinned.map(([, it]) => ({ label: it.plain, shortcut: this.keyOf(it), run: () => it.run() })) });
    items.push({ sep: true }, ...this.layoutItems());
    return items;
  }

  // ---- kéo thả trên thanh --------------------------------------------------------------------

  onMouseDown(e) {
    const src = e.target.closest('[data-idx]');
    if (!src || !this.el.contains(src)) return;
    e.preventDefault();   // giữ focus trong editor (như trước)
    if (e.button !== 0) return;
    const sx = e.clientX, sy = e.clientY;
    let drag = null;
    const move = (ev) => {
      if (!drag) {
        if (Math.abs(ev.clientX - sx) + Math.abs(ev.clientY - sy) < 6) return;
        this.shell.closeMenu();
        drag = { src, tok: src.dataset.tok || '|', mark: Object.assign(document.createElement('div'), { className: 'shTbDropMark' }), drop: null };
        document.body.appendChild(drag.mark);
        src.classList.add('shTbDragging');
        document.body.classList.add('shTbDraggingBody');
      }
      this.dragOver(drag, ev);
    };
    const finish = (apply) => {
      document.removeEventListener('mousemove', move, true);
      document.removeEventListener('mouseup', up, true);
      document.removeEventListener('keydown', esc, true);
      if (!drag) return;
      drag.mark.remove();
      src.classList.remove('shTbDragging');
      document.body.classList.remove('shTbDraggingBody');
      this.el.querySelectorAll('.shTbDropInto').forEach((x) => x.classList.remove('shTbDropInto'));
      this.justDragged = true;
      setTimeout(() => { this.justDragged = false; }, 0);
      if (apply && drag.drop) this.applyDrop(+src.dataset.idx, drag.drop);
    };
    const up = () => finish(true);
    const esc = (ev) => { if (ev.key === 'Escape') { ev.preventDefault(); ev.stopImmediatePropagation(); finish(false); } };
    document.addEventListener('mousemove', move, true);
    document.addEventListener('mouseup', up, true);
    document.addEventListener('keydown', esc, true);
  }

  dragOver(drag, ev) {
    this.el.querySelectorAll('.shTbDropInto').forEach((x) => x.classList.remove('shTbDropInto'));
    drag.drop = null;
    drag.mark.style.display = 'none';
    const hit = document.elementFromPoint(ev.clientX, ev.clientY);
    const bar = this.el.getBoundingClientRect();
    let target = hit && hit.closest ? hit.closest('[data-idx]') : null;
    if (target && !this.el.contains(target)) target = null;
    if (!target) {
      // Chỗ trống của thanh (sau nút cuối): đưa xuống cuối.
      const inBar = ev.clientX >= bar.left && ev.clientX <= bar.right && ev.clientY >= bar.top && ev.clientY <= bar.bottom;
      const last = this.el.lastElementChild;
      if (!inBar || !last) return;
      const r = last.getBoundingClientRect();
      drag.drop = { mode: 'end' };
      Object.assign(drag.mark.style, { display: '', left: (r.right + 1) + 'px', top: r.top + 'px', height: r.height + 'px' });
      return;
    }
    if (target === drag.src) return;
    const r = target.getBoundingClientRect();
    const canInto = target.classList.contains('shTbGroup') && drag.tok !== '|' && !drag.tok.startsWith('grp:');
    const x = (ev.clientX - r.left) / r.width;
    if (canInto && x > 0.25 && x < 0.75) {
      target.classList.add('shTbDropInto');
      drag.drop = { mode: 'into', idx: +target.dataset.idx };
      return;
    }
    const after = x >= 0.5;
    drag.drop = { mode: after ? 'after' : 'before', idx: +target.dataset.idx };
    Object.assign(drag.mark.style, { display: '', left: (after ? r.right + 1 : r.left - 2) + 'px', top: r.top + 'px', height: r.height + 'px' });
  }

  applyDrop(from, drop) {
    const m = this.model();
    const tok = m.order[from];
    if (tok === undefined) return;
    if (drop.mode === 'into') {
      const g = m.groups.find((x) => 'grp:' + x.id === m.order[drop.idx]);
      if (!g) return;
      m.order.splice(from, 1);
      g.items.push(tok);
      this.commit(m);
      if (this.bcode && this.bcode.showToast) this.bcode.showToast(`Đã cho "${BcodeTbModel.label(tok, m)}" vào nhóm ${g.name}`, 2000);
      return;
    }
    let to = drop.mode === 'end' ? m.order.length : drop.idx + (drop.mode === 'after' ? 1 : 0);
    m.order.splice(from, 1);
    if (from < to) to--;
    m.order.splice(to, 0, tok);
    this.commit(m);
  }

  // ---- vị trí thanh --------------------------------------------------------------------------

  /// Đặt thanh vào đúng chỗ theo layout.toolbarPos. Ẩn thanh menu thì "cùng hàng menu" quay về vị trí trên.
  applyPosition() {
    const s = this.shell;
    const P = this.prefs;
    let pos = this.pos();
    if (pos === 'menu' && !P.layoutOn('menubar')) pos = 'top';
    let bottom = document.getElementById('shellBottom');
    if (pos === 'bottom') {
      if (!bottom) {
        bottom = Object.assign(document.createElement('div'), { id: 'shellBottom' });
        document.getElementById('app').appendChild(bottom);
      }
      if (this.el.parentElement !== bottom) bottom.appendChild(this.el);
    } else if (pos === 'menu') {
      if (this.el.parentElement !== s.menubar) s.menubar.insertBefore(this.el, s.menuRight || null);   // nằm trước nút 💡 Hint ở góc phải
    } else if (this.el.parentElement !== s.top) s.top.insertBefore(this.el, s.breadcrumb);
    if (bottom) bottom.style.display = pos === 'bottom' && P.layoutOn('toolbar') ? '' : 'none';
    this.el.classList.toggle('shTbInline', pos === 'menu');
    s.menubar.classList.toggle('shMenubarWithTb', pos === 'menu');
    return pos;
  }

  injectCss() {
    if (document.getElementById('shTbCss')) return;
    const st = document.createElement('style');
    st.id = 'shTbCss';
    st.textContent = [
      '#shellBottom { flex: 0 0 auto; background: var(--bc-panel-alt); border-top: 1px solid var(--bc-border); user-select: none; }',
      '#shellBottom .shToolbar { padding: 3px 6px; }',
      // Cùng hàng menu: thanh lấp phần còn lại của hàng, quá dài thì nút tự xuống dòng bên trong thanh (tiêu đề menu giữ cao 26px).
      '.shMenubar.shMenubarWithTb { height: auto; min-height: 26px; align-items: flex-start; }',
      '.shMenubarWithTb > .shMenuTitle { height: 26px; }',
      '.shToolbar.shTbInline { flex: 1 1 0; min-width: 160px; padding: 1px 6px; margin-left: 8px; border-left: 1px solid var(--bc-border); }',
      '.shTbIc { display: inline-block; min-width: 1.15em; text-align: center; margin-right: 5px; }',
      '.shToolbar.shTbIcon .shTbIc { margin-right: 0; }',
      '.shToolbar.shTbIcon .shTbBtn { padding: 3px 6px; }',
      '.shTbAbbr { font-size: 9.5px; font-weight: 700; line-height: 14px; border: 1px solid var(--bc-text-muted); border-radius: 3px; padding: 0 2px; }',
      '.shTbGroup .shTbCaret { opacity: 0.7; margin-left: 4px; font-size: 10px; }',
      '.shTbBtn.open { background: var(--bc-selection); border-color: var(--bc-border); }',
      '.shTbBtn.shTbDragging { opacity: 0.35; }',
      '.shTbBtn.shTbDropInto { outline: 2px solid var(--bc-accent); outline-offset: -1px; }',
      'body.shTbDraggingBody, body.shTbDraggingBody * { cursor: grabbing !important; }',
      '.shTbDropMark { position: fixed; width: 2px; background: var(--bc-accent); z-index: 22000; pointer-events: none; }',
      '.shItem.kb { background: var(--bc-selection); }',
      // Nút 💡 Hint ở góc phải thanh menu (shell.js buildTop).
      '.shMenuRight { margin-left: auto; display: flex; align-items: center; padding-right: 4px; height: 26px; flex: 0 0 auto; }',
      '.shMenuHint { display: flex; align-items: center; gap: 5px; padding: 0 9px; height: 22px; font-size: 13px; color: var(--bc-text); cursor: pointer; border-radius: 3px; }',
      '.shMenuHint:hover { background: var(--bc-selection); }',
      '.shMenuHint svg { color: #e5c07b; flex: 0 0 auto; }',
      // Settings → Bố cục: danh sách nút có nhóm.
      '.setTbRow.grp { background: color-mix(in srgb, var(--bc-accent) 14%, var(--bc-panel)); font-weight: 600; }',
      '.setTbRow.inGrp { margin-left: 28px; }',
      '.setTbRow.overInto { border-color: var(--bc-accent); box-shadow: inset 0 -3px 0 var(--bc-accent); }',
      '.setTbRow.end { justify-content: center; }',
      '.setTbRow .setTbName { flex: 0 1 220px; width: auto; min-width: 80px; padding: 2px 6px; font-weight: 600; }',
    ].join('\n');
    document.head.appendChild(st);
  }
}
