// The bottom panel that Problems and Find-in-Files results share, plus the row-rendering
// helpers both of them use. Kept apart from either feature because the two would otherwise
// each grow their own idea of how tall the panel is and which one is showing — and two
// owners of one strip of screen is how a panel ends up opening on top of itself.

class BcodeDock {
  constructor() {
    this.panel = document.getElementById('bottomPanel');
    this.tabs = document.getElementById('bottomTabs');
    this.body = document.getElementById('bottomBody');
    this.current = null;
    this.panes = new Map(); // id -> { tab, pane, title }

    this.buildChrome();
    this.setupResize();
  }

  buildChrome() {
    const spacer = document.createElement('div');
    spacer.id = 'bottomSpacer';
    const close = document.createElement('span');
    close.id = 'bottomClose';
    close.textContent = '✕';
    close.title = 'Đóng bảng (Esc)';
    close.onclick = () => this.hide();
    this.tabs.appendChild(spacer);
    this.tabs.appendChild(close);
  }

  /// Registers one pane. <paramref>id</paramref> must match the element id already in
  /// index.html — the markup owns the layout, this owns which one is visible.
  register(id, title) {
    const pane = document.getElementById(id);
    const tab = document.createElement('div');
    tab.className = 'dockTab';
    tab.onclick = () => this.show(id);

    const label = document.createElement('span');
    label.textContent = title;
    tab.appendChild(label);

    // Inserted before the spacer so tabs stay left-aligned and the ✕ stays at the far right.
    this.tabs.insertBefore(tab, document.getElementById('bottomSpacer'));
    this.panes.set(id, { tab, pane, title });
    return pane;
  }

  /// The small count next to a tab's name ("PROBLEMS 3"). Passing 0 or null removes it —
  /// a badge reading "0" is noise that looks like a state.
  setBadge(id, count) {
    const entry = this.panes.get(id);
    if (!entry) return;
    let badge = entry.tab.querySelector('.badge');
    if (!count) { if (badge) badge.remove(); return; }
    if (!badge) {
      badge = document.createElement('span');
      badge.className = 'badge';
      entry.tab.appendChild(badge);
    }
    badge.textContent = count > 999 ? '999+' : String(count);
  }

  show(id) {
    this.panel.style.display = 'flex';
    this.current = id;
    for (const [key, entry] of this.panes) {
      const active = key === id;
      entry.tab.classList.toggle('active', active);
      entry.pane.classList.toggle('active', active);
    }
  }

  hide() {
    this.panel.style.display = 'none';
    this.current = null;
  }

  isVisible(id) {
    return this.panel.style.display !== 'none' && (!id || this.current === id);
  }

  /// Show if hidden or if another pane is in front; hide only when this pane is already
  /// the one you are looking at. Same rule VSCode's panel toggles follow, and the reason
  /// Ctrl+Shift+M on a background Problems tab brings it forward instead of closing the
  /// search results you were reading.
  toggle(id) {
    if (this.isVisible(id)) this.hide();
    else this.show(id);
  }

  setupResize() {
    // A 4px grab strip along the panel's top edge. Its own element rather than a CSS
    // resize handle: the panel is a flex child with a fixed height, and `resize: vertical`
    // on such an element fights the flex layout instead of following the cursor.
    const grip = document.createElement('div');
    grip.style.cssText = 'position:absolute;left:0;right:0;top:-2px;height:5px;cursor:row-resize;z-index:5;';
    this.panel.style.position = 'relative';
    this.panel.appendChild(grip);
    grip.addEventListener('mousedown', (down) => {
      down.preventDefault();
      const startY = down.clientY;
      const startH = this.panel.getBoundingClientRect().height;
      const onMove = (e) => {
        const h = Math.min(Math.max(startH + (startY - e.clientY), 80), window.innerHeight - 200);
        this.panel.style.height = h + 'px';
      };
      const onUp = () => {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
      };
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });
  }
}

/// One clickable row: a fixed-width location on the left, the text on the right, with the
/// matched span marked when the caller says where it is. Shared by the problem list, the
/// search results and the reference list so all three navigate and look identical.
///
/// The text is set through textContent and the mark is built as an element — never through
/// innerHTML. These rows carry file content, and a controller that happens to contain
/// "<img onerror=...>" is completely ordinary here.
function buildResultRow({ icon, iconClass, location, text, highlight, title, onClick }) {
  const row = document.createElement('div');
  row.className = 'resultRow';
  if (title) row.title = title;

  if (icon) {
    const ic = document.createElement('span');
    ic.className = 'icon ' + (iconClass || '');
    ic.textContent = icon;
    row.appendChild(ic);
  }

  if (location) {
    const loc = document.createElement('span');
    loc.className = 'loc';
    loc.textContent = location;
    row.appendChild(loc);
  }

  const txt = document.createElement('span');
  txt.className = 'txt';
  if (highlight && highlight.length > 0 && highlight.start >= 0) {
    const { start, length } = highlight;
    txt.appendChild(document.createTextNode(text.slice(0, start)));
    const mark = document.createElement('mark');
    mark.textContent = text.slice(start, start + length);
    txt.appendChild(mark);
    txt.appendChild(document.createTextNode(text.slice(start + length)));
  } else {
    txt.textContent = text;
  }
  row.appendChild(txt);

  if (onClick) row.onclick = onClick;
  return row;
}

window.BcodeDock = BcodeDock;
window.buildResultRow = buildResultRow;
