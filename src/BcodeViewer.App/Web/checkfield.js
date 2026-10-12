// "Check Field" (kiểu FCodeViewer): đọc khai báo <fields> của file Grid / Dir đang mở, đối chiếu với <views> (Grid: <field name="…"/> trong view; Dir: [biến] trong dòng <item value="…">) rồi liệt kê:
//   Declare (đã khai báo) · Duplicate Declare (khai báo trùng) · Allow Contain · View (có trong view) · Duplicate View (view trùng) — kèm đoạn XML của view để copy (<field name="x"/> hoặc [x].Label, [x]).
// Tab Header: nhãn HeaderV / HeaderE của các field. Với Grid có thể TICK / BỎ TICK cột View để thêm / bớt <field name="…"/> trong view; trường có trong view mà chưa khai báo thì tick Declare để thêm khai báo mẫu.
// Chỉ đọc source đang mở; mọi sửa đều qua editor (Ctrl+Z hoàn tác được).

class BcodeCheckField {
  constructor(bcode) {
    this.bcode = bcode;
    this.open = false;
    this.tab = 'design';
    this.sel = new Set();       // tên các dòng đang chọn
    this.filter = 'all';        // all | noview | dupdecl | dupview | missing
    this.data = null;
    this._anchor = null;
    this.injectCss();
  }

  esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

  injectCss() {
    if (document.getElementById('cfCss')) return;
    const s = document.createElement('style'); s.id = 'cfCss';
    s.textContent = `
#cfPanel { position: fixed; left: 0; right: 0; bottom: 0; z-index: 80; display: none; flex-direction: column; background: var(--bc-bg); color: var(--bc-text); border-top: 2px solid var(--bc-accent); font: 12px 'Segoe UI', sans-serif; }
#cfPanel.show { display: flex; }
#cfHead { display: flex; align-items: center; gap: 10px; padding: 5px 10px; background: var(--bc-accent); color: var(--bc-accent-text); font-weight: 600; }
#cfHead .grow { flex: 1; } #cfHead button { background: transparent; border: 1px solid rgba(255,255,255,.45); color: inherit; border-radius: 3px; padding: 1px 9px; cursor: pointer; }
#cfHead button:hover { background: rgba(255,255,255,.18); }
#cfTabs { display: flex; gap: 2px; padding: 4px 8px 0; background: var(--bc-panel); border-bottom: 1px solid var(--bc-border); align-items: flex-end; }
#cfTabs .t { padding: 4px 16px; cursor: pointer; color: var(--bc-text-muted); border: 1px solid transparent; border-bottom: none; border-radius: 3px 3px 0 0; }
#cfTabs .t.on { color: var(--bc-text); background: var(--bc-bg); border-color: var(--bc-border); font-weight: 600; }
#cfTabs .gap { flex: 1; } #cfTabs select { background: var(--bc-input); color: var(--bc-text); border: 1px solid var(--bc-border); border-radius: 3px; padding: 1px 4px; margin-bottom: 3px; }
#cfBody { flex: 1; min-height: 0; overflow: auto; }
#cfBody table { border-collapse: collapse; width: 100%; font-size: 12px; }
#cfBody th { position: sticky; top: 0; z-index: 1; background: var(--bc-panel-alt); border: 1px solid var(--bc-border); padding: 4px 8px; text-align: left; font-weight: 600; white-space: nowrap; }
#cfBody td { border: 1px solid color-mix(in srgb, var(--bc-border) 70%, transparent); padding: 2px 8px; white-space: nowrap; }
#cfBody td.c, #cfBody th.c { text-align: center; }
#cfBody td.n { color: var(--bc-text-muted); text-align: right; width: 34px; background: var(--bc-panel-alt); }
#cfBody td { user-select: none; -webkit-user-select: none; }   /* bôi đen theo DÒNG (kéo chuột qua nhiều dòng), không chọn chữ lẻ từng ô */
#cfBody.drag { cursor: default; }
#cfBody td.x { font-family: Consolas, monospace; color: #9cdcfe; cursor: text; }
#cfBody tr:hover td { background: color-mix(in srgb, var(--bc-selection) 50%, transparent); }
#cfBody tr.sel td { background: color-mix(in srgb, #e8a05a 20%, var(--bc-bg)); }
#cfBody tr.sel td.act { background: #e8a05a !important; color: #111; font-weight: 600; outline: 1px solid #b86d1a; outline-offset: -1px; }
#cfBody tr.sel td.act.x { color: #123; }
#cfBody th.act { background: #b86d1a; color: #fff; }
#cfBody td.hl { background: #c47f17 !important; }
#cfBody tr.bad td.f { color: #ff6b6b; font-weight: 600; } #cfBody tr.dup td.f { color: #e0a800; font-weight: 600; }
#cfBody input[type=checkbox] { accent-color: var(--bc-accent-hover); }
#cfBody input[disabled] { opacity: .85; }
#cfBar { display: none; gap: 8px; padding: 6px 10px; background: var(--bc-panel); border-top: 1px solid var(--bc-border); align-items: center; }
#cfBar.show { display: flex; } #cfBar input { flex: 1; background: var(--bc-input); color: var(--bc-text); border: 1px solid var(--bc-border); padding: 4px 8px; border-radius: 3px; font: 12px Consolas, monospace; }
#cfBar button { background: var(--bc-accent); color: var(--bc-accent-text); border: 0; padding: 4px 12px; border-radius: 3px; cursor: pointer; }
#cfStatus { padding: 3px 10px; color: var(--bc-text-muted); border-top: 1px solid var(--bc-border); background: var(--bc-panel); }
#cfGoto { position: absolute; left: 50%; top: 18%; transform: translateX(-50%); z-index: 85; width: 380px; background: var(--bc-panel); border: 1px solid var(--bc-accent); box-shadow: 0 8px 26px var(--bc-shadow, rgba(0,0,0,.5)); }
#cfGoto .gh { display: flex; align-items: center; gap: 8px; padding: 4px 10px; background: var(--bc-accent); color: var(--bc-accent-text); font-weight: 600; } #cfGoto .gh span { flex: 1; } #cfGoto .gh b { cursor: pointer; padding: 0 4px; }
#cfGoto .gb { padding: 10px 12px; display: flex; flex-direction: column; gap: 6px; }
#cfGoto input { background: var(--bc-input); color: var(--bc-text); border: 1px solid var(--bc-accent); padding: 5px 8px; font: 13px 'Segoe UI', sans-serif; outline: none; }
#cfGoto .gl { max-height: 220px; overflow: auto; border: 1px solid var(--bc-border); background: var(--bc-bg); }
#cfGoto .gl div { padding: 3px 8px; cursor: pointer; font: 12.5px 'Segoe UI', sans-serif; display: flex; justify-content: space-between; gap: 10px; } #cfGoto .gl div small { color: var(--bc-text-muted); }
#cfGoto .gl div.on, #cfGoto .gl div:hover { background: #f1c27d; color: #111; } #cfGoto .gl div.on small, #cfGoto .gl div:hover small { color: #444; }
#cfGoto .gf { display: flex; gap: 8px; justify-content: space-between; align-items: center; color: var(--bc-text-muted); font-size: 11px; }
#cfGoto button { background: var(--bc-button); color: var(--bc-text); border: 1px solid var(--bc-border); border-radius: 3px; padding: 2px 10px; cursor: pointer; }
#cfMenu { position: fixed; z-index: 90; min-width: 230px; background: var(--bc-panel); border: 1px solid var(--bc-border); box-shadow: 0 6px 18px var(--bc-shadow, rgba(0,0,0,.4)); padding: 3px; font: 12px 'Segoe UI', sans-serif; color: var(--bc-text); }
#cfMenu .mi { display: flex; justify-content: space-between; gap: 22px; padding: 4px 12px; cursor: pointer; } #cfMenu .mi:hover { background: var(--bc-selection); }
#cfMenu .mi kbd { color: var(--bc-text-muted); font: 11px 'Segoe UI'; } #cfMenu .sp { height: 1px; background: var(--bc-border); margin: 3px 2px; }
`;
    document.head.appendChild(s);
  }

  build() {
    if (this.panel) return;
    const p = document.createElement('div'); p.id = 'cfPanel';
    p.innerHTML = `
      <div id="cfHead"><span id="cfTitle">Check Field</span><span class="grow"></span><button id="cfRefresh" title="Đọc lại file đang mở">⟳ Làm mới</button><button id="cfClose" title="Đóng (Esc)">✕ Đóng</button></div>
      <div id="cfTabs"><span class="t on" data-t="design">Design</span><span class="t" data-t="header">Header</span><span class="gap"></span>
        <select id="cfFilter" title="Lọc danh sách"><option value="all">Tất cả</option><option value="noview">Chưa có trong View</option><option value="missing">Có trong View nhưng chưa khai báo</option><option value="dupdecl">Khai báo trùng</option><option value="dupview">View trùng</option></select></div>
      <div id="cfBody"></div>
      <div id="cfBar"><input id="cfBarText" readonly /><button id="cfBarCopy">Copy to Clipboard</button></div>
      <div id="cfStatus"></div>`;
    document.body.appendChild(p);
    this.panel = p;
    this.body = p.querySelector('#cfBody');
    p.querySelector('#cfClose').onclick = () => this.close();
    p.querySelector('#cfRefresh').onclick = () => this.refresh();
    p.querySelectorAll('#cfTabs .t').forEach((t) => { t.onclick = () => { this.tab = t.dataset.t; p.querySelectorAll('#cfTabs .t').forEach((x) => x.classList.toggle('on', x === t)); this.render(); }; });
    p.querySelector('#cfFilter').onchange = (e) => { this.filter = e.target.value; this.render(); };
    p.querySelector('#cfBarCopy').onclick = () => { this.copy(p.querySelector('#cfBarText').value, 'Đã copy vào clipboard.'); };
    this.body.addEventListener('mousedown', (e) => this.onDown(e));
    this.body.addEventListener('mouseover', (e) => this.onOver(e));
    document.addEventListener('mouseup', () => { if (this._drag) { this._drag = null; this.body.classList.remove('drag'); } });
    this.body.addEventListener('dblclick', (e) => { const tr = e.target.closest('tr[data-n]'); if (tr && !e.target.closest('input')) this.gotoName(tr.dataset.n); });
    this.body.addEventListener('contextmenu', (e) => this.onMenu(e));
    this.body.addEventListener('change', (e) => this.onToggle(e));
    document.addEventListener('keydown', (e) => this.onKey(e), true);
    document.addEventListener('mousedown', (e) => { if (!e.target.closest('#cfMenu')) this.closeMenu(); }, true);
  }

  toggle() { this.open ? this.close() : this.openPanel(); }

  async openPanel() {
    this.build();
    const main = document.getElementById('main');
    const top = main ? Math.round(main.getBoundingClientRect().top) : 70;
    this.panel.style.top = top + 'px';
    this.panel.classList.add('show');
    this.open = true;
    await this.refresh();
  }

  close() {
    if (!this.panel) return;
    this.closeGoto();
    this.panel.classList.remove('show'); this.open = false; this.closeMenu();
    this.panel.querySelector('#cfBar').classList.remove('show');
    try { this.bcode.editor.focus(); } catch { /* editor chưa sẵn sàng */ }
  }

  status(t) { if (this.panel) this.panel.querySelector('#cfStatus').textContent = t || ''; }

  // ---- phân tích ---------------------------------------------------------------------------------

  /// Entity (&Tên;) khai triển ra field / view thật như "Xem trước Dir" (nếu có).
  async expandIf(raw) {
    const hasEnt = /&(?!amp;|lt;|gt;|quot;|apos;)[A-Za-z_][\w.:$-]*;/.test(raw);
    const dp = window.bcodeDirPreview;
    if (!hasEnt || !dp || !dp.expand) return { text: raw, unresolved: 0 };
    const ctx = { cache: new Map(), unresolved: new Set() };
    try { const out = await dp.expand(raw, ctx); return { text: typeof out === 'string' ? out : raw, unresolved: ctx.unresolved.size }; }
    catch { return { text: raw, unresolved: 1 }; }
  }

  parse(xml) {
    const dp = window.bcodeDirPreview;
    if (dp && dp.parseXml) return dp.parseXml(xml);
    const doc = new DOMParser().parseFromString('<r>' + xml + '</r>', 'text/xml');
    return doc.getElementsByTagName('parsererror')[0] ? { error: 'XML lỗi' } : { doc };
  }

  async analyze() {
    const model = this.bcode.currentModel;
    if (!model) return { error: 'Chưa mở file nào.' };
    const orig = model.getValue();
    const masked = orig.replace(/<!--[\s\S]*?-->/g, (m) => m.replace(/[^\n]/g, ' '));   // comment → khoảng trắng, giữ nguyên offset
    const kind = /<\s*grid\b/i.test(masked) ? 'grid' : /<\s*dir\b/i.test(masked) ? 'dir' : /<\s*(lookup|filter|report)\b/i.test(masked) ? 'other' : 'unknown';
    const fieldsM = /<fields\b[\s\S]*?<\/fields>/i.exec(masked);
    const viewsM = /<views\b[\s\S]*?<\/views>/i.exec(masked);
    if (!fieldsM && !viewsM) return { error: 'File này không có <fields> hay <views> để kiểm tra (chỉ dùng cho Grid / Dir / Filter...).' };

    const fx = await this.expandIf(fieldsM ? fieldsM[0] : '');
    const vx = await this.expandIf(viewsM ? viewsM[0] : '');
    const fp = fx.text ? this.parse(fx.text) : { doc: null };
    const vp = vx.text ? this.parse(vx.text) : { doc: null };
    if (fp.error) return { error: 'Không đọc được <fields>: ' + fp.error };
    if (vp.error) return { error: 'Không đọc được <views>: ' + vp.error };

    const order = []; const rows = new Map();
    const row = (n) => { if (!rows.has(n)) { rows.set(n, { name: n, type: '', format: '', decl: 0, allowContain: false, view: 0, hdrV: '', hdrE: '', hdrAt: false }); order.push(n); } return rows.get(n); };
    if (fp.doc) {
      const fieldsEl = fp.doc.getElementsByTagName('fields')[0];
      for (const f of Array.from(fieldsEl ? fieldsEl.children : [])) {
        if (f.tagName !== 'field') continue;
        const n = f.getAttribute('name'); if (!n) continue;
        const r = row(n); r.decl++;
        if (r.decl === 1) {
          r.type = f.getAttribute('type') || '';
          r.format = f.getAttribute('dataFormatString') || '';
          const ac = (f.getAttribute('allowContain') || '').toLowerCase(); r.allowContain = ac === 'true' || ac === '1';
          const h = Array.from(f.children).find((x) => x.tagName === 'header');
          if (h) { r.hdrV = h.getAttribute('v') || ''; r.hdrE = h.getAttribute('e') || ''; r.hdrAt = true; }
          // Field external (tên bắc cầu kiểu ten_xx%l) có defaultValue rỗng ('') không cần dịch / nhập header → tab Header bỏ qua.
          const dv = (f.getAttribute('defaultValue') || '').replace(/\s/g, '');
          r.skipHeader = (f.getAttribute('external') || '').toLowerCase() === 'true' && (dv === "''" || dv === '""' || dv === '');
          // Cột ẩn (hidden="true" hoặc visible="false") cũng không cần header.
          const isTrue = (a) => ['true', '1'].includes((f.getAttribute(a) || '').toLowerCase());
          if (isTrue('hidden') || (f.getAttribute('visible') || '').toLowerCase() === 'false') r.skipHeader = true;
        }
      }
    }
    let fieldMode = false;
    if (vp.doc) {
      const viewsEl = vp.doc.getElementsByTagName('views')[0];
      if (viewsEl) {
        for (const f of Array.from(viewsEl.getElementsByTagName('field'))) { const n = f.getAttribute('name'); if (n) { row(n).view++; fieldMode = true; } }
        for (const it of Array.from(viewsEl.getElementsByTagName('item'))) {
          const v = (it.getAttribute('value') || '').replace(/&[A-Za-z_][\w.:$-]*;/g, '');
          const seen = new Set();
          for (const m of v.matchAll(/\[([^\]]+)\]/g)) { const n = m[1]; if (!seen.has(n)) { seen.add(n); row(n).view++; } }
        }
      }
    }
    const xmlStyle = kind === 'dir' && !fieldMode ? 'dir' : 'grid';
    // dòng khai báo đầu tiên của từng field trong file gốc (để Goto)
    const fStart = fieldsM ? fieldsM.index : 0;
    const out = order.map((n) => {
      const r = rows.get(n);
      const re = new RegExp('<field\\b[^>]*\\bname\\s*=\\s*"' + n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '"', 'i');
      re.lastIndex = 0; const m = re.exec(masked.slice(fStart)); r.line = m ? model.getPositionAt(fStart + m.index).lineNumber : 0;
      r.xml = '<field name="' + n + '"/>';   // luôn dạng <field name="x"/> (dán thẳng vào view của Grid), kể cả khi mở từ Dir
      return r;
    });
    return { kind, xmlStyle, rows: out, unresolved: fx.unresolved + vx.unresolved, hasViews: !!viewsM, hasFields: !!fieldsM, editableView: fieldMode || (kind === 'grid' && !!viewsM) };
  }

  async refresh() {
    this.status('Đang đọc file…');
    this.data = await this.analyze();
    this.sel = new Set([...this.sel].filter((n) => this.data.rows && this.data.rows.some((r) => r.name === n)));
    this.render();
  }

  // ---- hiển thị ----------------------------------------------------------------------------------

  visibleRows() {
    const d = this.data; if (!d || !d.rows) return [];
    const f = this.filter;
    return d.rows.filter((r) =>
      f === 'all' ? true : f === 'noview' ? (r.decl > 0 && r.view === 0) : f === 'missing' ? (r.decl === 0 && r.view > 0) : f === 'dupdecl' ? r.decl > 1 : f === 'dupview' ? r.view > 1 : true);
  }

  render() {
    if (!this.panel) return;
    const d = this.data;
    const title = this.panel.querySelector('#cfTitle');
    if (!d || d.error) {
      title.textContent = 'Check Field';
      this.body.innerHTML = '<div style="padding:18px;color:var(--bc-text-muted)">' + this.esc((d && d.error) || 'Chưa có dữ liệu.') + '</div>';
      this.status('');
      return;
    }
    const rows = this.visibleRows();
    title.textContent = 'Check Field (' + rows.length + ' / ' + d.rows.length + ')';
    const chk = (on, dis, attrs) => '<input type="checkbox"' + (on ? ' checked' : '') + (dis ? ' disabled' : '') + (attrs || '') + '>';
    if (this.tab === 'header') {
      const hdr = d.rows.filter((r) => (r.hdrAt || r.hdrV || r.hdrE) && !r.skipHeader);
      this.body.innerHTML = '<table><thead><tr><th class="c" style="width:34px"></th><th class="c" style="width:40px">…</th><th style="width:36%">HeaderV</th><th style="width:36%">HeaderE</th><th>Translate</th></tr></thead><tbody>' +
        hdr.map((r, i) => '<tr data-n="' + this.esc(r.name) + '" class="' + (this.sel.has(r.name) ? 'sel' : '') + '"><td class="n">' + (i + 1) + '</td><td class="c">' + chk(true, true) + '</td><td>' + this.esc(r.hdrV) + '</td><td>' + this.esc(r.hdrE) + '</td><td class="x" title="Field ' + this.esc(r.name) + '">' + (r.hdrE ? '' : '<i style="color:var(--bc-text-muted)">(chưa có HeaderE)</i>') + '</td></tr>').join('') + '</tbody></table>';
    } else {
      const editView = d.editableView && d.xmlStyle === 'grid';
      this.body.innerHTML = '<table><thead><tr><th class="c"></th><th style="min-width:160px">Field Name</th><th>Type</th><th>Format</th><th class="c">Declare</th><th class="c">Duplicate<br>Declare</th><th class="c">Allow<br>Contain</th><th class="c">View</th><th class="c">Duplicate<br>View</th><th style="width:100%"></th></tr></thead><tbody>' +
        rows.map((r, i) => {
          const bad = r.decl === 0 && r.view > 0, dup = r.decl > 1 || r.view > 1;
          return '<tr data-n="' + this.esc(r.name) + '" class="' + (this.sel.has(r.name) ? 'sel ' : '') + (bad ? 'bad ' : dup ? 'dup' : '') + '">' +
            '<td class="n">' + (i + 1) + '</td><td class="f" data-c="name">' + this.esc(r.name) + '</td><td data-c="type">' + this.esc(r.type) + '</td><td data-c="format" title="' + this.esc(r.format) + '">' + this.esc(r.format.length > 18 ? r.format.slice(0, 18) + '…' : r.format) + '</td>' +
            '<td class="c" data-c="declare">' + chk(r.decl > 0, r.decl > 0 || !d.hasFields, ' data-k="decl"') + '</td><td data-c="dupdecl" class="c' + (r.decl > 1 ? ' hl' : '') + '" title="' + (r.decl > 1 ? 'Khai báo ' + r.decl + ' lần trong &lt;fields&gt; — xoá bớt' : '') + '">' + chk(r.decl > 1, true) + '</td><td class="c" data-c="allow">' + chk(r.allowContain, true) + '</td>' +
            '<td class="c" data-c="view">' + chk(r.view > 0, !editView, ' data-k="view"') + '</td><td data-c="dupview" class="c' + (r.view > 1 ? ' hl' : '') + '" title="' + (r.view > 1 ? 'Có ' + r.view + ' lần trong view — xoá bớt' : '') + '">' + chk(r.view > 1, true) + '</td><td class="x" data-c="xml">' + this.esc(r.xml) + '</td></tr>';
        }).join('') + '</tbody></table>';
    }
    this.paintSel();
    const miss = d.rows.filter((r) => r.decl === 0 && r.view > 0).length, nov = d.rows.filter((r) => r.decl > 0 && r.view === 0).length;
    const dd = d.rows.filter((r) => r.decl > 1).length, dv = d.rows.filter((r) => r.view > 1).length;
    this.status((d.kind === 'grid' ? 'Grid' : d.kind === 'dir' ? 'Dir' : 'File') + ' · ' + d.rows.length + ' trường · thiếu khai báo: ' + miss + ' · chưa có trong View: ' + nov + ' · khai báo trùng: ' + dd + ' · View trùng: ' + dv +
      (d.unresolved ? ' · ' + d.unresolved + ' entity không đọc được' : '') + (d.editableView && d.xmlStyle === 'grid' ? ' · tick cột View để thêm / bớt field trong view' : '') +
      ' · kéo chuột qua nhiều dòng để bôi đen (Ctrl+bấm: thêm / bớt, Shift+bấm: dải) · Ctrl+C: copy giá trị cột đang tô đậm · F6 / Ctrl+Shift+C: copy XML <field …/> · Ctrl+G: tới field · chuột phải: Goto / Copy');
  }

  // ---- chọn dòng / menu / phím -------------------------------------------------------------------

  rowNames() { return [...this.body.querySelectorAll('tr[data-n]')].map((x) => x.dataset.n); }
  paintSel() {
    const col = this._col || 'xml';
    this.body.querySelectorAll('tr[data-n]').forEach((x) => {
      const on = this.sel.has(x.dataset.n); x.classList.toggle('sel', on);
      x.querySelectorAll('td').forEach((td) => td.classList.toggle('act', on && (td.dataset.c || 'xml') === col && !td.classList.contains('n')));
    });
    this.body.querySelectorAll('th').forEach((th, i) => th.classList.toggle('act', this.tab === 'design' && this.sel.size > 0 && this._colIdx(col) === i));
    if (this.sel.size) this.status('Đã chọn ' + this.sel.size + ' dòng · cột đang tô đậm: "' + this.colLabel(col) + '" — Ctrl+C: copy giá trị cột này · F6 / Ctrl+Shift+C: copy XML <field …/>');
  }
  colLabel(c) { return { name: 'Field Name', type: 'Type', format: 'Format', declare: 'Declare', dupdecl: 'Duplicate Declare', allow: 'Allow Contain', view: 'View', dupview: 'Duplicate View', xml: 'XML' }[c] || 'XML'; }
  _colIdx(c) { return { name: 1, type: 2, format: 3, declare: 4, dupdecl: 5, allow: 6, view: 7, dupview: 8, xml: 9 }[c] || 0; }

  onDown(e) {
    if (e.button !== 0 || e.target.closest('input[type=checkbox]')) return;
    const tr = e.target.closest('tr[data-n]'); if (!tr) return;
    const n = tr.dataset.n, rows = this.rowNames();
    const td = e.target.closest('td'); this._col = (td && td.dataset.c) || 'xml';     // cột đang bôi đen → Ctrl+C copy đúng cột này
    if (e.shiftKey && this._anchor) {
      const a = rows.indexOf(this._anchor), b = rows.indexOf(n); const [s, t] = a < b ? [a, b] : [b, a];
      this.sel = new Set(rows.slice(s, t + 1));
    } else if (e.ctrlKey || e.metaKey) { this.sel.has(n) ? this.sel.delete(n) : this.sel.add(n); this._anchor = n; }
    else { this.sel = new Set([n]); this._anchor = n; this._drag = { base: new Set() }; this.body.classList.add('drag'); }
    this.paintSel();
    e.preventDefault();
    try { this.body.tabIndex = -1; this.body.focus({ preventScroll: true }); } catch { /* không lấy được focus — phím vẫn bắt ở document */ }
  }

  onOver(e) {
    if (!this._drag) return;
    const tr = e.target.closest('tr[data-n]'); if (!tr) return;
    const rows = this.rowNames(), a = rows.indexOf(this._anchor), b = rows.indexOf(tr.dataset.n); if (a < 0 || b < 0) return;
    const [s, t] = a < b ? [a, b] : [b, a];
    this.sel = new Set(rows.slice(s, t + 1)); this.paintSel();
  }

  closeMenu() { const m = document.getElementById('cfMenu'); if (m) m.remove(); }

  onMenu(e) {
    const tr = e.target.closest('tr[data-n]'); if (!tr) return;
    e.preventDefault();
    if (!this.sel.has(tr.dataset.n)) { this.sel = new Set([tr.dataset.n]); this._anchor = tr.dataset.n; this.body.querySelectorAll('tr[data-n]').forEach((x) => x.classList.toggle('sel', this.sel.has(x.dataset.n))); }
    this.closeMenu();
    const m = document.createElement('div'); m.id = 'cfMenu';
    const items = [['Goto Column …', 'Ctrl+G', () => this.gotoSelected()], ['Copy selected Column Name …', '', () => this.showNames(true)], ['Copy All Column Name …', '', () => this.showNames(false)], ['Copy giá trị cột đang tô đậm', 'Ctrl+C', () => this.copyColumn()], null, ['Copy Xml Column', 'F6 / Ctrl+Shift+C', () => this.copyXml()]];
    m.innerHTML = items.map((it, i) => it ? '<div class="mi" data-i="' + i + '"><span>' + this.esc(it[0]) + '</span><kbd>' + this.esc(it[1]) + '</kbd></div>' : '<div class="sp"></div>').join('');
    m.addEventListener('click', (ev) => { const mi = ev.target.closest('.mi'); if (mi) { this.closeMenu(); items[+mi.dataset.i][2](); } });
    document.body.appendChild(m);
    m.style.left = Math.min(e.clientX, innerWidth - 250) + 'px'; m.style.top = Math.min(e.clientY, innerHeight - 140) + 'px';
  }

  /// Ô nhập của CHÍNH bảng Check (ô Goto, thanh copy tên): ở đó Ctrl+C / Ctrl+A là của ô nhập. Còn textarea ẩn của Monaco (vẫn giữ focus sau khi bấm bảng) thì không tính.
  inOwnInput(el) { return !!(el && /INPUT|TEXTAREA/.test(el.tagName || '') && el.closest && el.closest('#cfPanel')); }

  onKey(e) {
    if (!this.open) return;
    if (e.key === 'Escape') { if (document.getElementById('cfGoto')) this.closeGoto(); else if (document.getElementById('cfMenu')) this.closeMenu(); else this.close(); e.preventDefault(); e.stopPropagation(); return; }
    if (document.getElementById('cfGoto')) return;     // đang gõ trong hộp Goto: các phím còn lại để hộp tự xử lý
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'c' && !this.inOwnInput(e.target)) { e.preventDefault(); e.stopPropagation(); if (e.shiftKey) this.copyXml(); else this.copyColumn(); return; }   // Ctrl+C = copy giá trị của CỘT đang tô đậm (tên / kiểu / format / XML...); Ctrl+Shift+C và F6 = copy XML <field name="x"/>
    if (e.key === 'F6') { e.preventDefault(); e.stopPropagation(); this.copyXml(); }
    else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'g') { e.preventDefault(); e.stopPropagation(); this.gotoSelected(); }
    else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'a' && !this.inOwnInput(e.target)) { e.preventDefault(); e.stopPropagation(); this.sel = new Set(this.visibleRows().map((r) => r.name)); this.render(); }
  }

  selectedRows() { const v = this.visibleRows(); const s = v.filter((r) => this.sel.has(r.name)); return s; }

  async copy(text, msg) {
    try { await navigator.clipboard.writeText(text); }
    catch { const a = document.createElement('textarea'); a.value = text; document.body.appendChild(a); a.select(); try { document.execCommand('copy'); } catch { /* không copy được */ } a.remove(); }
    this.status(msg || 'Đã copy.');
  }

  showNames(onlySelected) {
    const rows = onlySelected ? this.selectedRows() : this.visibleRows();
    if (!rows.length) { this.status('Chưa chọn dòng nào.'); return; }
    const bar = this.panel.querySelector('#cfBar'); bar.classList.add('show');
    const inp = bar.querySelector('#cfBarText'); inp.value = rows.map((r) => r.name).join(', '); inp.select();
  }

  /// Copy cột vừa bôi đen (tên field / Type / Format / XML) của các dòng đang chọn, mỗi dòng một dòng chữ — dán ra chỗ khác.
  copyColumn() {
    let rows = this.selectedRows(); if (!rows.length) rows = this.visibleRows();
    if (!rows.length) return;
    const col = this._col || 'xml';
    const val = (r) => ({ name: r.name, type: r.type, format: r.format, declare: r.decl > 0 ? 'True' : 'False', dupdecl: r.decl > 1 ? 'True' : 'False', allow: r.allowContain ? 'True' : 'False', view: r.view > 0 ? 'True' : 'False', dupview: r.view > 1 ? 'True' : 'False', xml: r.xml }[col] ?? r.xml);
    const label = this.colLabel(col);
    this.copy(rows.map(val).join('\r\n'), 'Đã copy ' + label + ' của ' + rows.length + ' dòng.');
  }

  copyXml() {
    let rows = this.selectedRows(); if (!rows.length) rows = this.visibleRows();
    if (!rows.length) return;
    const eol = '\r\n';
    this.copy(rows.map((r) => r.xml).join(eol), 'Đã copy XML của ' + rows.length + ' field'  + '.');
  }

  gotoSelected() { this.showGoto(); }

  // ---- hộp "Goto Column": gõ để lọc tên field, Enter = chọn dòng đó trong bảng; Ctrl+Enter (hoặc nút) = nhảy tới khai báo trong code ----
  showGoto() {
    if (!this.data || !this.data.rows) return;
    this.closeGoto();
    const box = document.createElement('div'); box.id = 'cfGoto';
    box.innerHTML = '<div class="gh"><span>Goto Column</span><b id="cfGotoX" title="Đóng (Esc)">✕</b></div><div class="gb"><label for="cfGotoIn">Column Name</label><input id="cfGotoIn" autocomplete="off" spellcheck="false" placeholder="gõ tên field để lọc…"><div class="gl" id="cfGotoList"></div>' +
      '<div class="gf"><span>↑↓ chọn · Enter: tới dòng trong bảng · Ctrl+Enter: tới khai báo trong code</span><span><button id="cfGotoCode">Mở trong code</button></span></div></div>';
    this.panel.appendChild(box);
    const inp = box.querySelector('#cfGotoIn'), list = box.querySelector('#cfGotoList');
    this._g = { items: [], on: 0 };
    const draw = () => {
      const q = inp.value.trim().toLowerCase();
      const all = this.data.rows;
      const starts = all.filter((r) => r.name.toLowerCase().startsWith(q)), has = all.filter((r) => !r.name.toLowerCase().startsWith(q) && r.name.toLowerCase().includes(q));
      this._g.items = (q ? starts.concat(has) : all).slice(0, 200); this._g.on = 0;
      list.innerHTML = this._g.items.length ? this._g.items.map((r, i) => '<div data-i="' + i + '" class="' + (i === 0 ? 'on' : '') + '"><span>' + this.esc(r.name) + '</span><small>' + this.esc(r.type || (r.decl ? '' : 'chưa khai báo')) + '</small></div>').join('') : '<div style="cursor:default"><small>Không có field nào khớp.</small></div>';
    };
    const mark = (n) => { this._g.on = Math.max(0, Math.min(this._g.items.length - 1, n)); list.querySelectorAll('div[data-i]').forEach((d, i) => { d.classList.toggle('on', i === this._g.on); if (i === this._g.on) d.scrollIntoView({ block: 'nearest' }); }); };
    const pickRow = () => { const r = this._g.items[this._g.on]; if (!r) return; this.closeGoto(); this.selectRow(r.name); };
    const pickCode = () => { const r = this._g.items[this._g.on]; if (!r) return; this.closeGoto(); this.gotoName(r.name); };
    inp.addEventListener('input', draw);
    inp.addEventListener('keydown', (e) => {
      if (e.key === 'ArrowDown') { e.preventDefault(); mark(this._g.on + 1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); mark(this._g.on - 1); }
      else if (e.key === 'Enter') { e.preventDefault(); e.ctrlKey ? pickCode() : pickRow(); }
    });
    list.addEventListener('click', (e) => { const d = e.target.closest('div[data-i]'); if (d) { this._g.on = +d.dataset.i; pickRow(); } });
    list.addEventListener('dblclick', (e) => { const d = e.target.closest('div[data-i]'); if (d) { this._g.on = +d.dataset.i; pickCode(); } });
    box.querySelector('#cfGotoCode').onclick = pickCode;
    box.querySelector('#cfGotoX').onclick = () => this.closeGoto();
    draw(); inp.focus();
  }
  closeGoto() { const g = document.getElementById('cfGoto'); if (g) g.remove(); }

  /// Chọn và cuộn tới 1 dòng trong bảng (bỏ bộ lọc nếu dòng đang bị ẩn).
  selectRow(name) {
    if (!this.visibleRows().some((r) => r.name === name)) { this.filter = 'all'; const f = this.panel.querySelector('#cfFilter'); if (f) f.value = 'all'; }
    this.sel = new Set([name]); this._anchor = name; this.render();
    const tr = [...this.body.querySelectorAll('tr[data-n]')].find((x) => x.dataset.n === name);
    if (tr) tr.scrollIntoView({ block: 'center' });
  }

  gotoName(name) {
    const r = this.data && this.data.rows.find((x) => x.name === name); if (!r) return;
    let line = r.line;
    if (!line) {   // không có khai báo: tìm chỗ dùng đầu tiên trong view
      const t = this.bcode.currentModel.getValue(); const i = t.search(new RegExp('name\\s*=\\s*"' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '"|\\[' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\]'));
      line = i >= 0 ? this.bcode.currentModel.getPositionAt(i).lineNumber : 0;
    }
    if (!line) { this.status('Không tìm thấy "' + name + '" trong file.'); return; }
    this.close();
    const ed = this.bcode.editor;
    const text = this.bcode.currentModel.getLineContent(line);
    const c = text.indexOf(name);
    ed.setSelection(new monaco.Range(line, c >= 0 ? c + 1 : 1, line, c >= 0 ? c + 1 + name.length : 1));
    ed.revealLineInCenter(line);
    ed.focus();
  }

  // ---- sửa view / khai báo ----------------------------------------------------------------------

  edit(range, text) {
    const ed = this.bcode.editor;
    ed.pushUndoStop(); ed.executeEdits('checkfield', [{ range, text }]); ed.pushUndoStop();
  }

  async onToggle(e) {
    const cb = e.target.closest('input[data-k]'); if (!cb) return;
    const tr = cb.closest('tr[data-n]'); const name = tr.dataset.n; const k = cb.dataset.k;
    const model = this.bcode.currentModel; const text = model.getValue();
    const masked = text.replace(/<!--[\s\S]*?-->/g, (m) => m.replace(/[^\n]/g, ' '));
    const eol = model.getEOL ? model.getEOL() : '\n';
    const q = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    if (k === 'view') {
      const vs = masked.search(/<views\b/i), ve = masked.indexOf('</views>', vs);
      if (vs < 0 || ve < 0) { this.status('Không có <views> để sửa.'); cb.checked = !cb.checked; return; }
      if (cb.checked) {
        const close = masked.indexOf('</view>', vs); if (close < 0 || close > ve) { this.status('Không tìm thấy </view> để thêm field.'); cb.checked = false; return; }
        const ls = masked.lastIndexOf('\n', close) + 1; const lead = /^[ \t]*/.exec(masked.slice(ls))[0];
        const own = masked.slice(ls, close).trim().length === 0;     // </view> nằm riêng 1 dòng
        const pos = model.getPositionAt(own ? ls : close);
        this.edit(new monaco.Range(pos.lineNumber, pos.column, pos.lineNumber, pos.column), own ? lead + (lead.includes('\t') ? '\t' : '  ') + '<field name="' + name + '"/>' + eol : '<field name="' + name + '"/>');
      } else {
        const re = new RegExp('[ \\t]*<field\\s+name\\s*=\\s*"' + q + '"\\s*/>[ \\t]*\\r?\\n?', 'g'); re.lastIndex = vs;
        let m, last = null; while ((m = re.exec(masked)) && m.index < ve) last = m;
        if (!last) { this.status('Không tìm thấy <field name="' + name + '"/> trong view (có thể do entity).'); cb.checked = true; return; }
        const a = model.getPositionAt(last.index), b = model.getPositionAt(last.index + last[0].length);
        this.edit(new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column), '');
      }
    } else if (k === 'decl' && cb.checked) {
      const fe = masked.search(/<\/fields>/i); if (fe < 0) { this.status('Không có <fields> để thêm khai báo.'); cb.checked = false; return; }
      const ls = masked.lastIndexOf('\n', fe) + 1; const lead = /^[ \t]*/.exec(masked.slice(ls))[0]; const own = masked.slice(ls, fe).trim().length === 0;
      const pos = model.getPositionAt(own ? ls : fe);
      const u = lead.includes('\t') ? '\t' : '  ';
      const body = lead + u + '<field name="' + name + '">' + eol + lead + u + u + '<header v="' + name + '" e="' + name + '"></header>' + eol + lead + u + '</field>' + (own ? eol : '');
      this.edit(new monaco.Range(pos.lineNumber, pos.column, pos.lineNumber, pos.column), body);
    }
    await new Promise((r) => setTimeout(r, 30));
    await this.refresh();
  }
}
