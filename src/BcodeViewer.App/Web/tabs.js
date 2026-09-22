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

  render() {
    const paths = this.bcode.openPaths();
    this.strip.classList.toggle('hasTabs', paths.length > 0);
    this.strip.innerHTML = '';

    // Same file name in two folders is the norm here (every project has its own
    // Voucher.xml), so a repeated caption gets its parent folder appended — the minimum
    // that makes the two tabs tellable apart without widening every tab to a full path.
    const nameCounts = new Map();
    for (const p of paths) {
      const n = fileNameOf(p);
      nameCounts.set(n, (nameCounts.get(n) || 0) + 1);
    }

    for (const path of paths) {
      const doc = this.bcode.docs.get(path);
      const name = fileNameOf(path);
      const tab = document.createElement('div');
      tab.className = 'edTab' + (path === this.bcode.activePath ? ' active' : '') + (doc.dirty ? ' dirty' : '');
      tab.title = path + (doc.dirty ? '\n(chưa lưu)' : '');

      const label = document.createElement('span');
      label.className = 'tabName';
      label.textContent = nameCounts.get(name) > 1
        ? `${name} — ${fileNameOf(dirNameOf(path))}`
        : name;

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
