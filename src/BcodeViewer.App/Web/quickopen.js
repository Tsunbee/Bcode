// Ctrl+P — Quick Open: gõ một phần tên/đường dẫn để mở nhanh file trong App_Data của project đang mở,
// kiểu bảng lệnh của VS Code (hộp nổi ở giữa phía trên, theo theme). Danh sách file do host quét
// (EditorBridge.BeginListFiles, có cache) — trang chỉ lọc/xếp hạng trong bộ nhớ nên gõ không bị trễ.
//
// Lọc: mọi từ (cách nhau bằng khoảng trắng) phải có trong đường dẫn; xếp hạng ưu tiên tên file khớp từ
// đầu, rồi tên file chứa từ khoá, rồi đường dẫn; file đang mở gần đây (tab) lên đầu khi chưa gõ gì.

class BcodeQuickOpen {
  constructor(bcode) {
    this.bcode = bcode;
    this.files = [];
    this.root = null;
    bcode.editor.addAction({
      id: 'bcode.quickOpen',
      label: 'Mở nhanh File (Quick Open)',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyP],
      run: () => this.open(),
    });
    // Ctrl+P cả khi focus không nằm trong editor (cây file, panel...).
    document.addEventListener('keydown', (e) => {
      if (e.ctrlKey && !e.shiftKey && !e.altKey && (e.key === 'p' || e.key === 'P')) {
        if (document.activeElement && document.activeElement.closest && document.activeElement.closest('.monaco-editor')) return;
        e.preventDefault();
        this.open();
      }
    }, true);
  }

  async open() {
    if (this.overlay) { this.input.focus(); this.input.select(); return; }
    const root = this.bcode.appDataRoot(true);
    if (!root) { window.bcodeUi.alert('Mở 1 file trong App_Data của project trước (Quick Open tìm trong App_Data của file đang mở).'); return; }

    const overlay = document.createElement('div');
    overlay.className = 'qoOverlay';
    const box = document.createElement('div');
    box.className = 'qoBox';
    const input = document.createElement('input');
    input.className = 'qoInput';
    input.placeholder = 'Gõ tên file để mở (vd: SVTran, grid vctck)...';
    input.spellcheck = false;
    const status = document.createElement('div');
    status.className = 'qoStatus';
    status.textContent = 'Đang quét file trong App_Data...';
    const list = document.createElement('div');
    list.className = 'qoList';
    box.append(input, status, list);
    overlay.appendChild(box);
    document.body.appendChild(overlay);
    this.overlay = overlay;
    this.input = input;
    this.list = list;
    this.status = status;
    this.selected = 0;
    this.shown = [];
    input.focus();

    overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) this.close(); });
    input.addEventListener('input', () => { this.selected = 0; this.render(); });
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') { e.preventDefault(); this.close(); }
      else if (e.key === 'ArrowDown') { e.preventDefault(); this.move(1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); this.move(-1); }
      else if (e.key === 'PageDown') { e.preventDefault(); this.move(10); }
      else if (e.key === 'PageUp') { e.preventDefault(); this.move(-10); }
      else if (e.key === 'Enter') { e.preventDefault(); this.accept(this.shown[this.selected]); }
    });

    if (this.root !== root || !this.files.length) {
      try {
        this.files = JSON.parse(await window.bcodeHost.call('BeginListFiles', root, false));
        this.root = root;
      } catch (e) {
        status.textContent = 'Không quét được App_Data: ' + e;
        return;
      }
    }
    if (!this.overlay) return; // closed while scanning
    this.render();
  }

  close() {
    if (!this.overlay) return;
    this.overlay.remove();
    this.overlay = null;
    this.bcode.editor.focus();
  }

  move(delta) {
    if (!this.shown.length) return;
    this.selected = Math.max(0, Math.min(this.shown.length - 1, this.selected + delta));
    this.highlight();
  }

  rank(query) {
    const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
    const recent = new Set(this.bcode.openPaths ? this.bcode.openPaths().map((p) => p.toLowerCase()) : []);
    const prefix = this.root.replace(/\\+$/, '') + '\\';
    if (!terms.length) {
      // Chưa gõ gì: file đang mở (tab) trước, rồi theo ABC.
      const open = [...recent].filter((p) => p.startsWith(prefix.toLowerCase())).map((p) => p.slice(prefix.length));
      const openSet = new Set(open);
      return this.files.filter((f) => openSet.has(f.toLowerCase())).concat(this.files.filter((f) => !openSet.has(f.toLowerCase()))).slice(0, 200);
    }
    const scored = [];
    for (const f of this.files) {
      const lower = f.toLowerCase();
      if (!terms.every((t) => lower.includes(t))) continue;
      const name = lower.slice(lower.lastIndexOf('\\') + 1);
      let score = 0;
      for (const t of terms) score += name.startsWith(t) ? 30 : name.includes(t) ? 15 : 1;
      if (recent.has((prefix + f).toLowerCase())) score += 5;
      score -= f.length / 1000; // tie-break: shorter path first
      scored.push([score, f]);
    }
    scored.sort((a, b) => b[0] - a[0]);
    return scored.slice(0, 200).map((s) => s[1]);
  }

  render() {
    const q = this.input.value.trim();
    this.shown = this.rank(q);
    this.list.textContent = '';
    const terms = q.toLowerCase().split(/\s+/).filter(Boolean);
    for (let i = 0; i < this.shown.length; i++) {
      const rel = this.shown[i];
      const cut = rel.lastIndexOf('\\');
      const row = document.createElement('div');
      row.className = 'qoItem';
      const name = document.createElement('span');
      name.className = 'qoName';
      this.mark(name, rel.slice(cut + 1), terms);
      const dir = document.createElement('span');
      dir.className = 'qoDir';
      dir.textContent = cut > 0 ? rel.slice(0, cut) : '';
      row.append(name, dir);
      row.onmousemove = () => { if (this.selected !== i) { this.selected = i; this.highlight(); } };
      row.onclick = () => this.accept(rel);
      this.list.appendChild(row);
    }
    this.status.textContent = this.files.length
      ? `${this.shown.length >= 200 ? '200+' : this.shown.length} / ${this.files.length} file  ·  ↑↓ chọn  ·  Enter mở  ·  Esc đóng`
      : 'Không có file nào trong App_Data.';
    this.highlight();
  }

  /// Tô đậm phần khớp từ khoá trong tên file.
  mark(el, text, terms) {
    if (!terms.length) { el.textContent = text; return; }
    const lower = text.toLowerCase();
    const hits = new Array(text.length).fill(false);
    for (const t of terms) {
      let i = lower.indexOf(t);
      while (i >= 0) { for (let k = i; k < i + t.length; k++) hits[k] = true; i = lower.indexOf(t, i + t.length); }
    }
    let buf = '', on = false;
    const flush = () => {
      if (!buf) return;
      const s = document.createElement(on ? 'b' : 'span');
      s.textContent = buf;
      el.appendChild(s);
      buf = '';
    };
    for (let k = 0; k < text.length; k++) {
      if (hits[k] !== on) { flush(); on = hits[k]; }
      buf += text[k];
    }
    flush();
  }

  highlight() {
    const rows = this.list.children;
    for (let i = 0; i < rows.length; i++) rows[i].classList.toggle('selected', i === this.selected);
    const row = rows[this.selected];
    if (row) row.scrollIntoView({ block: 'nearest' });
  }

  accept(rel) {
    if (!rel) return;
    const path = this.root.replace(/\\+$/, '') + '\\' + rel;
    this.close();
    this.bcode.openFile(path);
  }
}
