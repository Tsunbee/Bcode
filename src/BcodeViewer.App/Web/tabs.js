// The tab strip above the editor. Pure view: every piece of state it draws lives in
// BcodeEditor.docs (see editor.js), and every action it offers is one of that class's
// methods — so the strip can be re-rendered from scratch at any time without coordinating
// with anything, which is exactly what render() does.
//
// Why not reuse the WinForms tree on the left as the only switcher: that tree lists
// *recently opened* files per project, which is a different set from *currently open*
// ones, and it has no notion of unsaved state. Conflating "in my history" with "loaded in
// the editor" is what made a closed file look open (and the reverse) before this existed.

class BcodeTabs {
  constructor(bcode) {
    this.bcode = bcode;
    this.strip = document.getElementById('tabStrip');
    this.menu = null;

    // Horizontal wheel scrolling: with more than a handful of tabs the strip overflows,
    // and a trackpad/wheel gesture over it should move it rather than do nothing.
    this.strip.addEventListener('wheel', (e) => {
      if (e.deltaY === 0) return;
      e.preventDefault();
      this.strip.scrollLeft += e.deltaY;
    }, { passive: false });

    document.addEventListener('click', () => this.hideMenu(), true);
    this.render();
  }

  /// What the strip lists. Normally just the open documents. With the left tree hidden
  /// ("Show Vertical Tabpage" off) the strip takes over the tree's job too: every file in
  /// the tree, in tree order, followed by any open file the tree doesn't list — so hiding
  /// the tree moves the whole file list into this row instead of making it unreachable.
  items() {
    const shell = window.bcodeShell;
    if (!shell || !shell.sidebarHidden()) return this.bcode.openPaths().map((path) => ({ path }));

    const out = [];
    const seen = new Set();
    const walk = (nodes, group) => {
      for (const n of nodes || []) {
        if (n.kind === 'group') walk(n.children, n.text.replace(/\s*\(\d+\)$/, ''));
        else if (n.kind === 'file') {
          if (!n.path || seen.has(n.path.toLowerCase())) continue;
          seen.add(n.path.toLowerCase());
          out.push({ path: n.path, key: n.key, group });
        } else walk(n.children, group);
      }
    };
    walk(shell.tree.nodes, '');
    for (const path of this.bcode.openPaths()) {
      if (!seen.has(path.toLowerCase())) out.push({ path });
    }
    return out;
  }

  /// Đường dẫn (chữ thường) → mã project: nhóm cấp ngoài cùng của cây file ("VPMILK (7)" → "VPMILK").
  projectMap() {
    const map = new Map();
    const shell = window.bcodeShell;
    const walk = (nodes, project) => {
      for (const n of nodes || []) {
        if (n.kind === 'file') { if (n.path && !map.has(n.path.toLowerCase())) map.set(n.path.toLowerCase(), project); }
        else walk(n.children, project);
      }
    };
    for (const top of (shell && shell.tree && shell.tree.nodes) || []) {
      if (top.kind === 'group') walk(top.children, String(top.text || '').replace(/\s*\(\d+\)$/, ''));
      else walk([top], '');
    }
    return map;
  }

  render() {
    const items = this.items();
    const projects = this.projectMap();
    this.strip.classList.toggle('hasTabs', items.length > 0);
    this.strip.innerHTML = '';

    // Same file name in two folders is the norm here (every project has its own
    // Voucher.xml), so a repeated caption gets its project (tree files) or parent folder
    // appended — the minimum that makes the two tabs tellable apart.
    const nameCounts = new Map();
    const nameProjects = new Map(); // tên file → các project có file trùng tên đó
    for (const it of items) {
      const n = fileNameOf(it.path);
      nameCounts.set(n, (nameCounts.get(n) || 0) + 1);
      it.project = projects.get(it.path.toLowerCase()) || '';
      if (!nameProjects.has(n)) nameProjects.set(n, new Set());
      nameProjects.get(n).add(it.project);
    }
    // Cùng tên file ở NHIỀU project (kể cả khi cây dọc đang bật, không có nhóm để suy ra) thì thêm mã project vào trước:
    // "VPMILK · SVTran.xml — Grid". Chỉ trùng trong 1 project thì giữ như cũ (chỉ thêm thư mục).
    const labelOf = (path, group, project) => {
      const name = fileNameOf(path);
      if (!(nameCounts.get(name) > 1)) return name;
      const prefix = project && nameProjects.get(name).size > 1 ? `${project} · ` : '';
      return `${prefix}${name} — ${group || fileNameOf(dirNameOf(path))}`;
    };

    for (const { path, key, group, project } of items) {
      const doc = this.bcode.docs.get(path);
      if (!doc) { this.strip.appendChild(this.renderClosedTab(path, key, group, labelOf(path, group, project))); continue; }
      const name = fileNameOf(path);
      const tab = document.createElement('div');
      tab.className = 'edTab' + (path === this.bcode.activePath ? ' active' : '') + (doc.dirty ? ' dirty' : '');
      tab.title = path + (doc.dirty ? '\n(chưa lưu)' : '');

      const label = document.createElement('span');
      label.className = 'tabName';
      label.textContent = labelOf(path, group, project);

      const close = document.createElement('span');
      close.className = 'tabClose';
      // A dot rather than an ✕ while there are unsaved changes, swapping to the ✕ on
      // hover: the marker and the close button share one slot (there is no room for both
      // at this width), and the dot makes the accidental click land on something that
      // looks unclickable instead of on a close button.
      close.textContent = doc.dirty ? '●' : '✕';
      close.title = 'Đóng (Ctrl+W)';
      close.onmouseenter = () => { close.textContent = '✕'; };
      close.onmouseleave = () => { close.textContent = doc.dirty ? '●' : '✕'; };
      close.onclick = (e) => { e.stopPropagation(); this.bcode.closeDoc(path); };

      tab.appendChild(label);
      tab.appendChild(close);
      tab.onclick = () => this.bcode.activateDoc(path);
      // Middle click closes, the way it does in every browser and in VSCode. auxclick
      // rather than mousedown: mousedown on button 1 also triggers autoscroll.
      tab.onauxclick = (e) => { if (e.button === 1) { e.preventDefault(); this.bcode.closeDoc(path); } };
      tab.oncontextmenu = (e) => { e.preventDefault(); this.showMenu(e.clientX, e.clientY, path); };

      this.strip.appendChild(tab);
      if (path === this.bcode.activePath) {
        // Ctrl+Tab can land on a tab that scrolled out of view.
        requestAnimationFrame(() => tab.scrollIntoView({ block: 'nearest', inline: 'nearest' }));
      }
    }
  }

  /// A tree file that isn't loaded yet (only listed while the tree is hidden). Click opens
  /// it through the host exactly like clicking it in the tree; ✕ drops it from the list,
  /// same as the tree's own ✕.
  renderClosedTab(path, key, group, labelText) {
    const tab = document.createElement('div');
    tab.className = 'edTab notOpen';
    tab.title = path;

    const label = document.createElement('span');
    label.className = 'tabName';
    label.textContent = labelText;

    const close = document.createElement('span');
    close.className = 'tabClose';
    close.textContent = '✕';
    close.title = 'Bỏ khỏi danh sách';
    const remove = () => window.bcodeShell.cmd('tree.remove', key);
    close.onclick = (e) => { e.stopPropagation(); remove(); };

    tab.append(label, close);
    tab.onclick = () => window.bcodeShell.cmd('tree.open', path);
    tab.onauxclick = (e) => { if (e.button === 1) { e.preventDefault(); remove(); } };
    tab.oncontextmenu = (e) => e.preventDefault();
    return tab;
  }

  showMenu(x, y, path) {
    this.hideMenu();
    const items = [
      ['Đóng', () => this.bcode.closeDoc(path)],
      ['Đóng các tab khác', () => this.bcode.closeOthers(path)],
      ['Đóng tất cả', () => this.bcode.closeAll()],
      null,
      ['Mở ở khung tách', () => {
        if (!this.bcode.editorSecondary) this.bcode.toggleSplit();
        this.bcode.showInSplit(path);
      }],
      ['Copy đường dẫn', () => navigator.clipboard.writeText(path)],
      ['Mở thư mục chứa file', () => window.chrome.webview.hostObjects.host.OpenFolder(path)],
    ];

    // Reuses the context-menu look already defined for the editor's own right-click menu
    // (contextmenu.js / .ctxMenu in style.css) so there is one menu style in the app.
    const menu = document.createElement('div');
    menu.className = 'ctxMenu';
    menu.style.position = 'fixed';
    for (const item of items) {
      if (!item) {
        const sep = document.createElement('div');
        sep.className = 'ctxSep';
        menu.appendChild(sep);
        continue;
      }
      const [text, action] = item;
      const row = document.createElement('div');
      row.className = 'ctxItem';
      row.innerHTML = '<span></span>';
      row.firstChild.textContent = text;
      row.onclick = () => { this.hideMenu(); action(); };
      menu.appendChild(row);
    }

    document.body.appendChild(menu);
    // Placed after measuring, so a right-click near the bottom/right edge doesn't open a
    // menu half off screen.
    const rect = menu.getBoundingClientRect();
    menu.style.left = Math.min(x, window.innerWidth - rect.width - 4) + 'px';
    menu.style.top = Math.min(y, window.innerHeight - rect.height - 4) + 'px';
    this.menu = menu;
  }

  hideMenu() {
    if (this.menu) { this.menu.remove(); this.menu = null; }
  }
}

window.BcodeTabs = BcodeTabs;
