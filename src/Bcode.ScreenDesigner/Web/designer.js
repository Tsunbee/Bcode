// Giao diện Designer. "Xem trước Dir" (BcodeDirPreview) của BcodeViewer lo toàn bộ phần dựng form + kéo-thả đổi chỗ / chèn trường / khay biến chưa dùng;
// file này thêm: chọn màn hình chuẩn, tạo thiết kế mới (danh mục / chứng từ), thêm trường / tab, xuất ảnh, lưu thiết kế.
(async function () {
  const $ = (id) => document.getElementById(id);
  const call = (m, ...a) => window.bcodeHost.call(m, ...a);
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const escAttr = (s) => esc(s).replace(/'/g, '&#39;');

  const bcode = new FakeBcode();
  window.bcodeViewer = bcode;
  window.bcodeEntity = new BcodeEntity(bcode);
  const preview = new BcodeDirPreview(bcode);
  window.bcodeDirPreview = preview;
  preview.palOpen = true;
  preview.openPanel();

  const S = { source: '', project: '', screens: [], current: null, viewer: '', state: {}, workspaces: [] };
  let toastTimer = null, codeTimer = null;

  function toast(t, err) { const el = $('toast'); el.textContent = t; el.className = err ? 'err' : ''; el.style.display = 'block'; clearTimeout(toastTimer); toastTimer = setTimeout(() => { el.style.display = 'none'; }, err ? 8000 : 3500); }
  const status = (t) => { $('status').textContent = t || ''; };
  const getText = () => (bcode.currentModel ? bcode.currentModel.getValue() : '');
  const saveState = () => call('BeginSaveState', JSON.stringify({ source: S.source, useMirror: !!S.useMirror, recent: S.state.recent || [], custom: (S.custom || []).map(({ id, name, src }) => ({ id, name, src })), last: S.current ? { k: S.current.k, n: S.current.n } : null, zoom: $('zoom').value, vars: $('optVar').checked, tests: S.tests || {}, mineDir: S.state.mineDir || '', lastPack: S.state.lastPack || '' })).catch(() => {});

  // ------------------------------------------------------------------ nguồn / project
  function renderProjects() {
    const opts = [];
    const seen = new Set();
    const add = (name, src) => { if (!src || seen.has(src.toLowerCase())) return; seen.add(src.toLowerCase()); opts.push({ name, src }); };
    S.workspaces.forEach((w) => add((w.name || w.id) + '  —  ' + w.source, w.source));
    (S.state.recent || []).forEach((r) => add(r, r));
    add(S.source, S.source);
    $('proj').innerHTML = opts.map((o) => '<option value="' + escAttr(o.src) + '"' + (o.src.toLowerCase() === S.source.toLowerCase() ? ' selected' : '') + '>' + esc(o.name) + '</option>').join('');
  }

  const rootOf = () => S.source;
  const applyCache = () => call('BeginSetCache', S.source, S.useMirror ? '1' : '').catch(() => {});
  /// Danh sách màn hình: có cache thì hiện NGAY (không chạm ổ mạng), cache cũ hơn 1 ngày hoặc bấm ⟳ thì quét lại ở nền.
  async function loadScreens(force) {
    if (!S.source) { $('list').innerHTML = '<div class="empty">Chọn thư mục source (nút “Chọn source…”).</div>'; return; }
    S.root = rootOf();
    status(force ? 'Đang quét lại danh sách…' : 'Đang đọc danh sách…');
    let r;
    try { r = JSON.parse(await call('BeginListScreens', S.root, force ? '1' : '')); }
    catch (e) { S.screens = []; $('list').innerHTML = '<div class="empty">' + esc(e.message || e) + '</div>'; status(''); return; }
    S.screens = r.items; renderList();
    status(S.screens.length + ' màn hình' + (S.useMirror ? ' · ⚡ có bộ nhớ đệm' : '') + (r.cached ? ' · nhớ lúc ' + r.stamp : ''));
    const age = r.cached ? (Date.now() - new Date(String(r.stamp).replace(' ', 'T')).getTime()) : 0;
    if (r.cached && !force && age > 24 * 3600 * 1000) {
      try { const f = JSON.parse(await call('BeginListScreens', S.root, '1')); if (f.items.length !== S.screens.length) { S.screens = f.items; renderList(); } status(S.screens.length + ' màn hình · vừa cập nhật'); } catch { /* giữ danh sách cũ */ }
    }
  }

  const kinds = () => [...document.querySelectorAll('#kinds input[data-k]')].filter((c) => c.checked).map((c) => c.dataset.k);
  function renderList() {
    const q = $('q').value.trim().toLowerCase(), ks = new Set(kinds()), onlyTran = $('onlyTran').checked;
    const items = S.screens.filter((s) => ks.has(s.k) && (!onlyTran || /Tran$/i.test(s.n)) &&
      (!q || s.n.toLowerCase().includes(q) || (s.t || '').toLowerCase().includes(q)));
    $('cnt').textContent = '(' + items.length + ')';
    $('list').innerHTML = items.slice(0, 400).map((s, i) =>
      '<div class="it' + (S.current && S.current.p === s.p ? ' sel' : '') + '" data-i="' + S.screens.indexOf(s) + '"><span class="n">' + esc(s.n) + '<span class="k">' + esc(s.k) + '</span>' + (S.mineByFrom && S.mineByFrom[s.k + ':' + s.n] ? '<span class="k" style="color:var(--bc-accent)" title="Bạn đã lưu bản sửa của màn hình này — bấm để mở bản của bạn">✎ bản của bạn</span>' : '') + '</span><span class="t">' + esc(s.t || '') + '</span></div>').join('') +
      (items.length > 400 ? '<div class="empty">… còn ' + (items.length - 400) + ' màn hình — gõ thêm để lọc.</div>' : '') || '<div class="empty">Không có màn hình nào khớp.</div>';
  }
  $('q').addEventListener('input', renderList);
  document.querySelectorAll('#kinds input').forEach((c) => c.addEventListener('change', renderList));
  $('list').addEventListener('click', (e) => { const it = e.target.closest('.it'); if (it) openScreen(S.screens[+it.dataset.i]); });

  $('proj').onchange = async () => { S.source = $('proj').value; await refreshMirror(); applyCache(); await loadScreens(false); saveState(); };
  $('bBrowse').onclick = async () => {
    const p = await call('BeginPickFolder', S.source || '');
    if (!p) return;
    S.source = p; S.state.recent = [p, ...(S.state.recent || []).filter((x) => x.toLowerCase() !== p.toLowerCase())].slice(0, 8);
    renderProjects(); await refreshMirror(); applyCache(); await loadScreens(false); saveState();
  };

  // ------------------------------------------------------------------ mở / tạo màn hình
  async function openScreen(s, force) {
    const mineName = !force && S.mineByFrom && S.mineByFrom[s.k + ':' + s.n];
    if (mineName) {
      await openMine(mineName); S.origScreen = s; renderList();
      toast('Đang mở bản BẠN đã sửa của ' + s.n + ' (mẫu “' + mineName + '”). Bấm “📄 Xem bản gốc” để xem bản của source.');
      return;
    }
    S.origScreen = null; $('bOrig').style.display = 'none';
    status('Đang mở ' + s.n + '…');
    let text;
    try { text = String(await call('BeginReadFile', s.p) || '').replace(/^\uFEFF/, ''); }
    catch (e) { toast('Không đọc được file: ' + (e.message || e), true); status(''); return; }
    S.current = s;
    S.test = (S.tests && S.tests[s.p]) || {};
    bcode.setText(text, s.p);
    text = await flatten(text);
    if (text !== getText()) bcode.setText(text, s.p);
    syncAll();
    renderList();
    status(s.k + ' · ' + s.n);
    saveState();
  }

  /// Nhiều controller chuẩn dựng <fields> / <views> bằng entity (&ExtraFields.Master;, &BI.Form.View.Customer;…) nằm ở file Include — không có <view id="Dir"> ngay trong
  /// file nên kéo-thả không có chỗ để sửa. Khai triển 2 khối này thành XML thật 1 lần khi mở: thiết kế là bản "phẳng" (đúng nội dung đang chạy, có thể sửa / lưu).
  async function flatten(text) {
    const needs = /<views\b[\s\S]*?<\/views>/i.exec(text);
    if (!needs) return text;
    let out = text;
    const swap = async (tag) => {
      const m = new RegExp('<' + tag + '\\b[\\s\\S]*?</' + tag + '>', 'i').exec(out);
      if (!m || !/&[A-Za-z_][\w.:$-]*;/.test(m[0].replace(/&(amp|lt|gt|quot|apos);/g, ''))) return;
      try {
        const full = await preview.expand(m[0], { cache: new Map(), unresolved: new Set() });
        if (typeof full === 'string' && full.includes('<' + tag)) out = out.slice(0, m.index) + full + out.slice(m.index + m[0].length);
      } catch (e) { console.warn('flatten ' + tag, e); }
    };
    await swap('views');
    await swap('fields');
    return out;
  }

  function syncAll() {
    const t = getText();
    const m = /<title\b[^>]*\bv="([^"]*)"/i.exec(t);
    if (document.activeElement !== $('tTitle')) $('tTitle').value = m ? decode(m[1]) : '';
    if ($('right').style.display !== 'none' && document.activeElement !== $('code')) $('code').value = t;
    $('bUndo').disabled = !bcode.editor.undoStack.length; $('bRedo').disabled = !bcode.editor.redoStack.length;
    $('bViewer').disabled = !(S.current && S.current.p && !S.current.isNew);
  }
  const decode = (s) => s.replace(/&quot;/g, '"').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
  const encode = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;');
  bcode.editor.onDidChangeModelContent(() => syncAll());

  // đổi tiêu đề cửa sổ = sửa <title v="…">
  $('tTitle').addEventListener('change', () => {
    const t = getText(); if (!t) return;
    const v = encode($('tTitle').value);
    bcode.replaceAll(/<title\b[^>]*\bv="[^"]*"/i.test(t) ? t.replace(/(<title\b[^>]*\bv=")[^"]*"/i, (_, a) => a + v + '"') : t);
  });

  // ------------------------------------------------------------------ Undo / Redo
  $('bUndo').onclick = () => bcode.editor.undo();
  $('bRedo').onclick = () => bcode.editor.redo();
  document.addEventListener('keydown', (e) => {
    if (e.target && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)) return;
    if (e.ctrlKey && e.key.toLowerCase() === 'z') { e.preventDefault(); bcode.editor.undo(); }
    else if (e.ctrlKey && e.key.toLowerCase() === 'y') { e.preventDefault(); bcode.editor.redo(); }
  });

  // ------------------------------------------------------------------ zoom / biến / mã XML
  const applyZoom = () => { $('dpRoot').style.zoom = (+$('zoom').value / 100); saveState(); };
  $('zoom').onchange = applyZoom;
  $('optVar').onchange = () => { $('dpRoot').classList.toggle('noVar', !$('optVar').checked); saveState(); };
  $('bCode').onclick = () => {
    const r = $('right'); const show = r.style.display === 'none';
    r.style.display = show ? 'flex' : 'none';
    if (show) { $('code').value = getText(); $('codeInfo').textContent = ''; }
  };
  $('bApply').onclick = () => { bcode.replaceAll($('code').value); $('codeInfo').textContent = 'Đã áp dụng ' + new Date().toLocaleTimeString(); };
  $('code').addEventListener('keydown', (e) => { if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); $('bApply').click(); } });

  // ------------------------------------------------------------------ tạo thiết kế mới
  const slug = (s) => s.normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '');

  function fieldXml(name, vn, en, opt = {}) {
    let attrs = '', items = '';
    if (opt.key) attrs += ' isPrimaryKey="true"';
    if (opt.req) attrs += ' allowNulls="false"';
    if (opt.ro) attrs += ' readOnly="true"';
    if (opt.cat) attrs += ' categoryIndex="' + opt.cat + '"';
    if (opt.type === 'num') { attrs += ' type="Decimal" dataFormatString="@baseCurrencyAmountInputFormat" clientDefault="0"'; items = '<items style="Numeric"/>'; }
    else if (opt.type === 'date') attrs += ' type="DateTime" dataFormatString="@datetimeFormat" align="left"';
    else if (opt.type === 'bool') attrs += ' type="Boolean"';
    else if (opt.type === 'lookup') items = '<items style="AutoComplete" controller="' + esc(opt.ctl || 'Customer') + '" reference="ten_' + name + '%l" key="status = \'1\'" check="1 = 1"/>';
    else if (opt.type === 'grid') items = '<items style="Grid" controller="' + esc(opt.ctl) + '" row="1"/>';
    else if (opt.type === 'mask') items = '<items style="Mask"/>';
    else if (opt.type === 'drop') items = '<items style="DropDownList">\n        <item value="Lựa chọn 1"/>\n        <item value="Lựa chọn 2"/>\n      </items>';
    else if (opt.type === 'area') attrs += ' rows="3"';
    return '    <field name="' + name + '"' + attrs + '>\n      <header v="' + encode(vn) + '" e="' + encode(en) + '"></header>\n' + (items ? '      ' + items + '\n' : '') + '    </field>';
  }
  const nameFieldXml = (name, cat) => '    <field name="ten_' + name + '%l" readOnly="true" external="true" defaultValue="\'\'"' + (cat ? ' categoryIndex="' + cat + '"' : '') + '>\n      <header v="" e=""></header>\n    </field>';

  function scaffold(o) {
    const base = slug(o.title) || (o.kind === 'ct' ? 'chung_tu' : 'danh_muc');
    const title = o.title || (o.kind === 'ct' ? 'chứng từ' : 'danh mục');
    const F = [], R = [], C = [];
    const W = '110, 200, 30, 110, 200';       // 2 cột trường: nhãn | ô | đệm | nhãn | ô
    const two = (a, b) => '11011: [' + a + '].Label, [' + a + '], [' + b + '].Label, [' + b + ']';
    const one = (a) => '11000: [' + a + '].Label, [' + a + ']';
    const look = (a) => '11010: [' + a + '].Label, [' + a + '], [ten_' + a + '%l]';
    const cat = (n) => (o.tabs ? n : 0);
    if (o.kind === 'dm') {
      F.push(fieldXml('ma_' + base, 'Mã', 'Code', { key: true, req: true, type: 'mask' }), fieldXml('ten_' + base, 'Tên', 'Name', { req: true }),
        fieldXml('ten_' + base + '2', 'Tên khác', 'Other name'), fieldXml('ghi_chu', 'Ghi chú', 'Note'), fieldXml('status', 'Sử dụng', 'In use', { type: 'bool' }));
      R.push(one('ma_' + base), one('ten_' + base), one('ten_' + base + '2'), one('ghi_chu'), one('status'));
      if (o.tabs) {
        F.push(fieldXml('nh_1', 'Nhóm 1', 'Group 1', { type: 'lookup', ctl: 'Group', cat: 1 }), nameFieldXml('nh_1', 1),
          fieldXml('tk', 'Tài khoản', 'Account', { type: 'lookup', ctl: 'Account', cat: 1 }), nameFieldXml('tk', 1),
          fieldXml('ngay_hl', 'Ngày hiệu lực', 'Effective date', { type: 'date', cat: 2 }), fieldXml('ghi_chu2', 'Ghi chú thêm', 'More notes', { cat: 2 }));
        R.push(look('nh_1'), look('tk'), two('ngay_hl', 'ghi_chu2'));
        C.push([1, 'Thông tin chung', 'General'], [2, 'Khác', 'Other']);
      }
    } else {
      F.push(fieldXml('ma_kh', 'Mã khách', 'Customer', { type: 'lookup', ctl: 'Customer', req: true }), nameFieldXml('ma_kh', 0),
        fieldXml('ngay_ct', 'Ngày lập', 'Date', { type: 'date', req: true }), fieldXml('so_ct', 'Số chứng từ', 'Voucher No.', { req: true }),
        fieldXml('ma_nt', 'Ngoại tệ', 'Currency', { type: 'lookup', ctl: 'Currency' }), nameFieldXml('ma_nt', 0),
        fieldXml('ty_gia', 'Tỷ giá', 'Rate', { type: 'num' }), fieldXml('dien_giai', 'Diễn giải', 'Description'),
        fieldXml('t_tien', 'Tổng tiền', 'Total', { type: 'num', ro: true }), fieldXml('t_thue', 'Tiền thuế', 'Tax', { type: 'num', ro: true }), fieldXml('t_tt', 'Tổng thanh toán', 'Payment', { type: 'num', ro: true }));
      R.push(look('ma_kh'), two('so_ct', 'ngay_ct'), look('ma_nt'), one('ty_gia'), one('dien_giai'));
      if (o.grid) { F.push(fieldXml('d81', '', '', { type: 'grid', ctl: o.grid, cat: cat(1) })); R.push('1: [d81]'); }
      if (o.tabs) {
        F.push(fieldXml('ma_thue', 'Mã thuế', 'Tax code', { type: 'lookup', ctl: 'Tax', cat: 2 }), nameFieldXml('ma_thue', 2), fieldXml('ghi_chu', 'Ghi chú', 'Note', { cat: 3 }));
        R.push(look('ma_thue'), one('ghi_chu'));
        C.push([1, 'Chi tiết', 'Detail'], [2, 'Thuế', 'Tax'], [3, 'Khác', 'Other']);
      }
      R.push(two('t_tien', 't_thue'), one('t_tt'));
    }
    const rows = R;
    const cats = C.length ? '      <categories>\n' + C.map(([i, v, e]) => '        <category index="' + i + '" columns="' + W + '" anchor="1">\n          <header v="' + v + '" e="' + e + '"/>\n        </category>').join('\n') + '\n      </categories>\n' : '';
    return '<?xml version="1.0" encoding="utf-8"?>\n<dir table="' + base + '" code="' + (o.kind === 'dm' ? 'ma_' + base : 'stt_rec') + '" order="' + (o.kind === 'dm' ? 'ma_' + base : 'ngay_ct') + '" xmlns="urn:schemas-fast-com:data-dir">\n' +
      '  <title v="' + encode(title) + '" e="' + encode(title) + '"></title>\n  <fields>\n' + F.join('\n') + '\n  </fields>\n  <views>\n    <view id="Dir" height="' + (80 + rows.length * 22) + '" anchor="1">\n      <item value="' + W + '"/>\n' +
      rows.map((r) => '      <item value="' + r + '"/>').join('\n') + '\n' + cats + '    </view>\n  </views>\n</dir>\n';
  }

  $('bNew').onclick = () => {
    const kind = document.querySelector('input[name=nk]:checked').value;
    const text = scaffold({ kind, title: $('nTitle').value.trim(), tabs: $('nTabs').checked, grid: $('nGrid').checked ? ($('nGridCtl').value.trim() || '') : '' });
    // đặt "đường dẫn ảo" trong thư mục Dir của source để lưới chi tiết (..\Grid\X.xml) và entity include vẫn tìm được
    const dir = (S.root || S.source) ? (S.root || S.source).replace(/[\\/]+$/, '') + '\\App_Data\\Controllers\\Dir' : 'C:\\Designer';
    S.current = { k: 'Dir', n: ($('nTitle').value.trim() || 'Thiết kế mới'), p: dir + '\\_new_design.f', isNew: true };
    S.test = {};
    bcode.setText(text, S.current.p);
    syncAll(); renderList(); status('Thiết kế mới — ' + (kind === 'ct' ? 'chứng từ' : 'danh mục'));
  };

  // ------------------------------------------------------------------ thêm trường / tab
  /// Mask hay dùng nhất của form hiện tại cho từng kiểu dòng: single (nhãn+ô) / lookup (nhãn+ô+tên) / two (2 cặp nhãn+ô). null nếu form chưa có dòng nào như vậy.
  function maskTemplate(text, kind) {
    const vi = viewInfo(text); if (!vi) return null;
    const body = text.slice(vi.start, vi.end), count = new Map();
    for (const m of body.matchAll(/<item\b[^>]*\bvalue\s*=\s*"([01]+):\s*([^"]*)"/g)) {
      const t = m[2].split(',').map((x) => x.trim());
      let k = null;
      if (t.length === 2 && /\]\.Label$/.test(t[0]) && /^\[[^\]]+\]$/.test(t[1])) k = 'single';
      else if (t.length === 3 && /\]\.Label$/.test(t[0]) && /^\[[^\]]+\]$/.test(t[1]) && /%l\]$/.test(t[2])) k = 'lookup';
      else if (t.length === 4 && /\]\.Label$/.test(t[0]) && /^\[[^\]]+\]$/.test(t[1]) && /\]\.Label$/.test(t[2]) && /^\[[^\]]+\]$/.test(t[3])) k = 'two';
      if (k === kind && m[1].length === vi.n) count.set(m[1], (count.get(m[1]) || 0) + 1);
    }
    let best = null, bn = 0; for (const [k, v] of count) if (v > bn) { best = k; bn = v; }
    return best;
  }

  function viewInfo(text) {
    const vs = /<view\b[^>]*\bid\s*=\s*"Dir"[^>]*>/i.exec(text);
    if (!vs) return null;
    const start = vs.index + vs[0].length;
    const end = text.indexOf('</view>', start);
    if (end < 0) return null;
    const body = text.slice(start, end);
    const widths = /<item\b[^>]*\bvalue\s*=\s*"([^":]*)"/i.exec(body);
    const n = widths ? widths[1].split(',').length : 5;
    const catIdx = body.search(/<categories\b/i);
    const cats = [...body.matchAll(/<category\b([^>]*)>/gi)].map((m) => ({ index: (/index\s*=\s*"([^"]*)"/.exec(m[1]) || [])[1], columns: (/columns\s*=\s*"([^"]*)"/.exec(m[1]) || [])[1] || '' }));
    return { start, end, n, widthsLine: widths ? widths[1].trim() : '', rowInsert: start + (catIdx >= 0 ? catIdx : body.length), hasCats: catIdx >= 0, cats };
  }

  function fillCatSelect() {
    const vi = viewInfo(getText());
    $('fCat').innerHTML = '<option value="">(không — nằm ở form chính)</option>' + ((vi && vi.cats) || []).filter((c) => c.index && c.index !== '-1').map((c) => '<option value="' + escAttr(c.index) + '">Tab ' + esc(c.index) + '</option>').join('');
  }

  $('bAddField').onclick = () => {
    if (!getText()) { toast('Mở hoặc tạo 1 màn hình trước.', true); return; }
    fillCatSelect(); $('fName').value = ''; $('fVn').value = ''; $('fEn').value = ''; $('fCtl').value = '';
    $('fReq').checked = false; $('fRo').checked = false; $('ov').style.display = 'flex'; $('fName').focus();
  };
  $('fCancel').onclick = () => { $('ov').style.display = 'none'; };
  $('fOk').onclick = () => {
    const name = slug($('fName').value || $('fVn').value);
    if (!name) { toast('Nhập tên biến hoặc nhãn.', true); return; }
    const text = getText();
    if (new RegExp('<field\b[^>]*\bname="' + name + '"').test(text)) { toast('Đã có trường tên "' + name + '".', true); return; }
    try {
      const r = insertField(text, { name, type: $('fType').value, vn: $('fVn').value || name, en: $('fEn').value, ctl: $('fCtl').value.trim() || 'Customer', req: $('fReq').checked, ro: $('fRo').checked, cat: $('fCat').value }, $('fPlace').checked);
      bcode.replaceAll(r.text);
      $('ov').style.display = 'none';
      toast(r.placed ? 'Đã thêm "' + name + '" ở cuối form — kéo để đổi chỗ.' : 'Đã thêm "' + name + '" vào khay “Biến chưa dùng” — kéo vào form.');
    } catch (err) { toast(err.message, true); }
  };

  /// Thêm <category> vào text, trả { text, idx } (null nếu view dựng bằng entity).
  function addTabTo(text, label) {
    const vi = viewInfo(text); if (!vi) return null;
    const idx = Math.max(0, ...vi.cats.map((c) => parseInt(c.index, 10)).filter((n) => n > 0 && n < 100)) + 1;
    const cat = '        <category index="' + idx + '" columns="' + esc(vi.widthsLine || '100, 200') + '" anchor="1">\n          <header v="' + encode(label) + '" e="' + encode(label) + '"/>\n        </category>\n';
    if (vi.hasCats) {
      const close = text.indexOf('</categories>', vi.start);
      text = text.slice(0, close) + cat + '      ' + text.slice(close);
    } else {
      text = text.slice(0, vi.end) + '      <categories>\n' + cat + '      </categories>\n    ' + text.slice(vi.end);
    }
    return { text, idx };
  }
  $('bAddTab').onclick = async () => {
    const text = getText(); if (!text) { toast('Mở hoặc tạo 1 màn hình trước.', true); return; }
    if (!viewInfo(text)) { toast('Màn hình này dựng view bằng entity nên không thêm tab tự động được — sửa ở “Mã XML”.', true); return; }
    const label = await ask('Tên tab (tiếng Việt)', 'Thông tin thêm'); if (!label) return;
    const r = addTabTo(text, label), idx = r.idx;
    bcode.replaceAll(r.text);
    toast('Đã thêm tab "' + label + '" (tab số ' + idx + '). Khi thêm trường chọn tab này để đưa trường vào.');
  };

  // ------------------------------------------------------------------ xuất ảnh / lưu
  async function renderPng() {
    const win = document.querySelector('#dpRoot .dpWindow');
    if (!win) throw new Error('Chưa có màn hình nào để chụp.');
    const css = await (await fetch('dp.css')).text();
    const clone = win.cloneNode(true);
    clone.querySelectorAll('.dpGrip,.dpPalette,.dpNewRow').forEach((n) => n.remove());
    clone.querySelectorAll('[draggable]').forEach((n) => n.removeAttribute('draggable'));
    ['dpSel', 'dpDrop', 'dpDragging', 'dpInsL', 'dpInsR', 'dpRowBefore', 'dpRowAfter'].forEach((c) => clone.querySelectorAll('.' + c).forEach((n) => n.classList.remove(c)));
    if (!$('optVar').checked) clone.querySelectorAll('.dpVar').forEach((n) => n.remove());
    const r = win.getBoundingClientRect(), z = +$('zoom').value / 100;
    const w = Math.ceil(r.width / z), h = Math.ceil(r.height / z);
    const xhtml = new XMLSerializer().serializeToString(clone);
    const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + h + '"><foreignObject x="0" y="0" width="' + w + '" height="' + h + '">' +
      '<div xmlns="http://www.w3.org/1999/xhtml" style="width:' + w + 'px;background:#fff;font:11px Tahoma,sans-serif"><style>' + css.replace(/</g, '&lt;') + '</style>' + xhtml + '</div></foreignObject></svg>';
    const img = new Image();
    await new Promise((res, rej) => { img.onload = res; img.onerror = () => rej(new Error('Không dựng được ảnh từ màn hình.')); img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg); });
    const scale = 2, canvas = document.createElement('canvas');
    canvas.width = w * scale; canvas.height = h * scale;
    const g = canvas.getContext('2d'); g.fillStyle = '#fff'; g.fillRect(0, 0, canvas.width, canvas.height); g.scale(scale, scale); g.drawImage(img, 0, 0);
    return canvas.toDataURL('image/png').split(',')[1];
  }
  const fileBase = () => (S.current ? S.current.n.replace(/[\\/:*?"<>|]+/g, '_') : 'man-hinh');
  async function exportPng(mode) {
    try {
      status('Đang chụp…');
      const b64 = await renderPng();
      const r = await call('BeginSavePng', fileBase() + '.png', b64, mode);
      status('');
      if (r === 'clipboard') toast('Đã copy ảnh — dán (Ctrl+V) vào Word.');
      else if (r) toast('Đã lưu ' + r);
    } catch (e) { status(''); toast(e.message || String(e), true); }
  }
  $('bCopy').onclick = () => exportPng('copy');
  $('bPng').onclick = () => exportPng('save');
  $('bOrig').onclick = () => {
    const f = S.current && S.current.from; if (!f) return;
    const s = S.origScreen || S.screens.find((x) => x.k + ':' + x.n === f);
    if (!s) { toast('Không tìm thấy màn hình gốc ' + f + ' trong danh sách.', true); return; }
    openScreen(s, true);
  };
  $('bViewer').onclick = () => { if (S.current && !S.current.isNew) call('BeginOpenFile', S.current.p, S.viewer); };

  // ------------------------------------------------------------------ hộp nhập chữ nhỏ (thay prompt của trình duyệt)
  function ask(title, value) {
    return new Promise((resolve) => {
      $('askTitle').textContent = title; $('askInput').value = value || '';
      $('askOv').style.display = 'flex'; $('askInput').focus(); $('askInput').select();
      const done = (v) => { $('askOv').style.display = 'none'; $('askOk').onclick = $('askCancel').onclick = $('askInput').onkeydown = null; resolve(v); };
      $('askOk').onclick = () => done($('askInput').value);
      $('askCancel').onclick = () => done(null);
      $('askInput').onkeydown = (e) => { if (e.key === 'Enter') done($('askInput').value); else if (e.key === 'Escape') done(null); };
    });
  }

  // ------------------------------------------------------------------ chèn trường vào văn bản (dùng chung: hộp ＋ Trường và các khối)
  /// Trả { text, placed } — thêm <field> vào <fields>, và (place) 1 dòng thiết kế ở cuối form (hoặc cuối tab cat).
  function insertField(text, o, place) {
    const xmlOpt = { type: o.type, ctl: o.ctl, req: o.req, ro: o.ro, cat: o.cat };
    let xml = fieldXml(o.name, o.vn, o.en || o.vn, xmlOpt);
    if (o.type === 'lookup') xml += '\n' + nameFieldXml(o.name, o.cat);
    const close = text.lastIndexOf('</fields>');
    if (close < 0) throw new Error('File này không có <fields>.');
    text = text.slice(0, close) + xml + '\n  ' + text.slice(close);
    let placed = false;
    if (place) {
      const vi = viewInfo(text);
      if (vi) {
        let n = vi.n;
        if (o.cat) { const c = vi.cats.find((x) => x.index === o.cat); if (c && /^[\d,\s]+$/.test(c.columns)) n = c.columns.split(',').length; }
        const withName = o.type === 'lookup' && n >= 5;
        let mask, tokens;
        if (o.type === 'label') { mask = '1' + '0'.repeat(Math.max(0, n - 1)); tokens = '[' + o.name + '].Label'; }
        else { mask = (!o.cat && maskTemplate(text, withName ? 'lookup' : 'single')) || (withName ? '11010' + '0'.repeat(n - 5) : (n >= 2 ? '11' + '0'.repeat(n - 2) : '1')); tokens = '[' + o.name + '].Label, [' + o.name + ']' + (withName ? ', [ten_' + o.name + '%l]' : ''); }
        const row = '      <item value="' + mask + ': ' + tokens + '"/>\n';
        text = text.slice(0, vi.rowInsert) + row + text.slice(vi.rowInsert); placed = true;
      }
    }
    return { text, placed };
  }
  const uniqueName = (text, prefix) => {
    let n = 1; while (new RegExp('<field\\b[^>]*\\bname="' + prefix + n + '"').test(text)) n++;
    return prefix + n;
  };

  // ------------------------------------------------------------------ khối thiết kế (kiểu FDesign)
  const BLOCKS = [
    { id: 'text', ic: 'Abc', name: 'TextBox', type: 'text', prefix: 'txt', vn: 'Trường văn bản' },
    { id: 'num', ic: '123', name: 'Numeric', type: 'num', prefix: 'so', vn: 'Số' },
    { id: 'date', ic: '▦ ngày', name: 'DateTime', type: 'date', prefix: 'ngay', vn: 'Ngày' },
    { id: 'bool', ic: '☑', name: 'Checkbox', type: 'bool', prefix: 'chk', vn: 'Lựa chọn' },
    { id: 'drop', ic: 'ab ▾', name: 'Dropbox', type: 'drop', prefix: 'ds', vn: 'Danh sách chọn' },
    { id: 'lookup', ic: '▤', name: 'Lookup', type: 'lookup', prefix: 'lk', vn: 'Mã tra cứu', ctl: 'Customer' },
    { id: 'area', ic: '¶', name: 'TextArea', type: 'area', prefix: 'ghi_chu', vn: 'Ghi chú' },
    { id: 'label', ic: 'Aa', name: 'Label', type: 'label', prefix: 'nhan', vn: 'Tiêu đề nhóm' },
    { id: 'grid', ic: '▥', name: 'Grid', click: 'grid' },
    { id: 'tab', ic: 'TAB', name: 'Tab', click: 'tab' },
  ];
  // Khối cấu trúc: nhiều trường + dòng thiết kế dựng sẵn (key = loại trường mẫu; r = các dòng, mỗi dòng 1–2 key)
  const P = {
    kh: { type: 'lookup', base: 'ma_kh', vn: 'Mã khách', ctl: 'Customer' }, ob: { type: 'text', base: 'ong_ba', vn: 'Người mua' },
    ngay: { type: 'date', base: 'ngay_ct', vn: 'Ngày lập' }, so: { type: 'text', base: 'so_ct', vn: 'Số chứng từ' },
    nt: { type: 'lookup', base: 'ma_nt', vn: 'Ngoại tệ', ctl: 'Currency' }, tg: { type: 'num', base: 'ty_gia', vn: 'Tỷ giá' },
    dg: { type: 'text', base: 'dien_giai', vn: 'Diễn giải' }, thue: { type: 'lookup', base: 'ma_thue', vn: 'Mã thuế', ctl: 'Tax' },
    tt: { type: 'num', base: 't_tien', vn: 'Tổng tiền', ro: true }, tth: { type: 'num', base: 't_thue', vn: 'Tiền thuế', ro: true }, ttt: { type: 'num', base: 't_tt', vn: 'Tổng thanh toán', ro: true },
    tk: { type: 'lookup', base: 'tk', vn: 'Tài khoản', ctl: 'Account' }, nh: { type: 'lookup', base: 'nh_1', vn: 'Nhóm', ctl: 'Group' },
    dt: { type: 'text', base: 'dien_thoai', vn: 'Điện thoại' }, fax: { type: 'text', base: 'fax', vn: 'Fax' }, em: { type: 'text', base: 'e_mail', vn: 'Email' },
    dc: { type: 'text', base: 'dia_chi', vn: 'Địa chỉ' }, mst: { type: 'text', base: 'ma_so_thue', vn: 'Mã số thuế' },
    ck1: { type: 'bool', base: 'chk', vn: 'Lựa chọn 1' }, ck2: { type: 'bool', base: 'chk', vn: 'Lựa chọn 2' }, ck3: { type: 'bool', base: 'chk', vn: 'Lựa chọn 3' },
    tn: { type: 'date', base: 'tu_ngay', vn: 'Từ ngày' }, dn: { type: 'date', base: 'den_ngay', vn: 'Đến ngày' },
    sl: { type: 'num', base: 'so_luong', vn: 'Số lượng' }, dgi: { type: 'num', base: 'don_gia', vn: 'Đơn giá' }, tien: { type: 'num', base: 'thanh_tien', vn: 'Thành tiền' },
    stk: { type: 'text', base: 'so_tk_nh', vn: 'Số tài khoản NH' }, nhg: { type: 'lookup', base: 'ma_nh', vn: 'Ngân hàng', ctl: 'Bank' }, cn: { type: 'text', base: 'chi_nhanh', vn: 'Chi nhánh' },
    vt: { type: 'lookup', base: 'ma_vt', vn: 'Mã vật tư', ctl: 'Item' }, dvt: { type: 'lookup', base: 'dvt', vn: 'ĐVT', ctl: 'Unit' }, kho: { type: 'lookup', base: 'ma_kho', vn: 'Kho', ctl: 'Warehouse' },
    st: { type: 'bool', base: 'status', vn: 'Sử dụng' },
    a: { type: 'text', base: 'truong', vn: 'Trường 1' }, b: { type: 'text', base: 'truong', vn: 'Trường 2' }, c: { type: 'text', base: 'truong', vn: 'Trường 3' }, d: { type: 'text', base: 'truong', vn: 'Trường 4' },
    ghi: { type: 'area', base: 'ghi_chu', vn: 'Ghi chú' }, gr: { type: 'label', base: 'nhan', vn: 'Nhóm thông tin' },
  };
  const COMPOSITES = [
    { id: 'c2', ic: 'A│B', name: '2 trường / dòng', r: [['a', 'b']] },
    { id: 'c4', ic: '2×2', name: '4 trường (2 dòng)', r: [['a', 'b'], ['c', 'd']] },
    { id: 'cgrp', ic: '▭ Aa', name: 'Nhóm + 2 trường', r: [['gr'], ['a', 'b']] },
    { id: 'ckh', ic: 'KH', name: 'Khách hàng', r: [['kh'], ['ob']] },
    { id: 'cdoc', ic: 'Ngày/Số', name: 'Ngày + Số CT', r: [['so', 'ngay']] },
    { id: 'cnt', ic: 'NT', name: 'Ngoại tệ + Tỷ giá', r: [['nt'], ['tg']] },
    { id: 'ctax', ic: '%', name: 'Thuế', r: [['thue'], ['tth']] },
    { id: 'ctot', ic: 'Σ', name: 'Tổng cộng', r: [['tt', 'tth'], ['ttt']] },
    { id: 'chead', ic: 'CT', name: 'Đầu chứng từ', r: [['kh'], ['ob'], ['so', 'ngay'], ['nt'], ['tg'], ['dg']] },
    { id: 'cdm', ic: 'DM', name: 'Đầu danh mục', r: [['tk'], ['nh'], ['a', 'b'], ['ghi']] },
    { id: 'clh', ic: '☎', name: 'Liên hệ', r: [['dt', 'fax'], ['em']] },
    { id: 'cdc', ic: 'ĐC', name: 'Địa chỉ + MST', r: [['dc'], ['mst', 'dt']] },
    { id: 'cck', ic: '☑☑', name: '3 checkbox', r: [['ck1', 'ck2'], ['ck3']] },
    { id: 'cdate', ic: 'Từ→Đến', name: 'Từ ngày – Đến ngày', r: [['tn', 'dn']] },
    { id: 'cqty', ic: 'SL×ĐG', name: 'SL – Đơn giá – Tiền', r: [['sl', 'dgi'], ['tien']] },
    { id: 'cbank', ic: 'NH', name: 'Ngân hàng', r: [['stk'], ['nhg'], ['cn']] },
    { id: 'cvt', ic: 'VT', name: 'Vật tư + ĐVT + Kho', r: [['vt'], ['dvt', 'kho']] },
    { id: 'cnote', ic: '¶✓', name: 'Ghi chú + Sử dụng', r: [['ghi'], ['st']] },
    { id: 'ctab', ic: 'TAB+', name: 'Tab + nội dung', tab: true, r: [['a', 'b'], ['c', 'd']] },
  ];
  S.custom = [];
  const allComps = () => COMPOSITES.concat(S.custom);
  function renderComps() {
    $('compGrid').innerHTML = allComps().map((b) => '<div class="blk comp' + (b.mine ? ' mine' : '') + '" draggable="true" data-id="' + escAttr(b.id) + '" title="' + (b.mine ? 'Khối của bạn — chuột phải để xoá. ' : '') + 'Kéo vào form hoặc bấm để thêm vào cuối"><span class="ic">' + esc(b.ic) + '</span><span class="nm">' + esc(b.name) + '</span></div>').join('') +
      '<div class="blk click" id="bMyBlock" title="Tạo khối cấu trúc riêng (lưu lại dùng lần sau)"><span class="ic">＋</span><span class="nm">Khối riêng…</span></div>';
  }
  renderComps();
  // 1 dòng = 1 dòng form; trong dòng, tối đa 2 trường cách nhau bằng " | "; mỗi trường: Nhãn[:loại[:Controller]] (loại: text, num, date, bool, area, lookup, label)
  function parseMine(name, t) {
    const r = [];
    for (const line of t.split(/\r?\n/)) {
      const parts = line.split('|').map((x) => x.trim()).filter(Boolean).slice(0, 2); if (!parts.length) continue;
      r.push(parts.map((p) => { const [vn, ty, ctl] = p.split(':').map((x) => x.trim()); const type = ['text', 'num', 'date', 'bool', 'area', 'lookup', 'label'].includes((ty || '').toLowerCase()) ? ty.toLowerCase() : 'text'; return { type, base: slug(vn) || 'truong', vn, ctl: ctl || (type === 'lookup' ? 'Customer' : undefined) }; }));
    }
    return { id: 'my_' + Date.now(), ic: name.slice(0, 5), name, mine: true, r, src: t };
  }
  document.addEventListener('click', async (e) => {
    if (!e.target.closest('#bMyBlock')) return;
    $('mbName').value = ''; $('mbRows').value = 'Mã đối tác:lookup:Customer\nGhi chú:area\nTừ ngày:date | Đến ngày:date'; $('mbOv').style.display = 'flex'; $('mbName').focus();
  });
  $('mbCancel').onclick = () => { $('mbOv').style.display = 'none'; };
  $('mbOk').onclick = () => {
    const name = $('mbName').value.trim(); if (!name) { toast('Nhập tên khối.', true); return; }
    const b = parseMine(name, $('mbRows').value); if (!b.r.length) { toast('Nhập ít nhất 1 dòng.', true); return; }
    S.custom.push(b); renderComps(); saveState(); $('mbOv').style.display = 'none'; toast('Đã lưu khối "' + name + '" — bấm hoặc kéo vào form để dùng.');
  };
  $('compGrid').addEventListener('contextmenu', async (e) => {
    const el = e.target.closest('.blk.mine'); if (!el) return; e.preventDefault();
    const v = await ask('Xoá khối "' + el.querySelector('.nm').textContent + '"? Gõ OK để xác nhận.', 'OK'); if (String(v || '').trim().toUpperCase() !== 'OK') return;
    S.custom = S.custom.filter((x) => x.id !== el.dataset.id); renderComps(); saveState();
  });

  async function addComposite(b) {
    let text = getText(); if (!text) { toast('Mở hoặc tạo 1 màn hình trước.', true); return; }
    let cat = '';
    if (b.tab) {
      const label = await ask('Tên tab (tiếng Việt)', 'Thông tin thêm'); if (!label) return;
      const t = addTabTo(text, label); if (!t) { toast('Màn hình dựng view bằng entity — không thêm tab tự động được.', true); return; }
      text = t.text; cat = String(t.idx);
    }
    const names = [];
    const taken = (n) => new RegExp('<field\\b[^>]*\\bname="' + n + '"').test(text) || names.includes(n);
    const uniq = (base) => { let n = base, i = 1; while (taken(n)) n = base + (++i); return n; };
    const rows0 = b.r.map((row) => row.map((k) => { const p = typeof k === 'string' ? P[k] : k; const nm = uniq(p.base); names.push(nm); return { nm, p }; }));
    for (const row of rows0) for (const x of row) {
      if (x.p.type === 'label') { text = insertField(text, { name: x.nm, type: 'text', vn: x.p.vn, cat }, false).text; continue; }
      text = insertField(text, { name: x.nm, type: x.p.type, vn: x.p.vn, ctl: x.p.ctl, ro: x.p.ro, cat }, false).text;
    }
    const vi = viewInfo(text); if (!vi) throw new Error('Không có <view id="Dir">.');
    let n = vi.n;
    if (cat) { const c = vi.cats.find((q) => q.index === cat); if (c && /^[\d,\s]+$/.test(c.columns)) n = c.columns.split(',').length; }
    const zeros = (k) => '0'.repeat(Math.max(0, k));
    const tok = (x) => (x.p.type === 'label' ? '[' + x.nm + '].Label' : '[' + x.nm + '].Label, [' + x.nm + ']');
    const rows = [];
    for (const row of rows0) {
      const tpl = (kind) => (cat ? null : maskTemplate(text, kind));
      const two = row.length === 2 && row.every((x) => x.p.type !== 'label');
      if (two && (tpl('two') || n >= 5)) rows.push((tpl('two') || '11011' + zeros(n - 5)) + ': ' + tok(row[0]) + ', ' + tok(row[1]));
      else for (const x of row) {
        const look = x.p.type === 'lookup' && (tpl('lookup') || n >= 5);
        const mask = x.p.type === 'label' ? '1' + zeros(n - 1) : (look ? (tpl('lookup') || '11010' + zeros(n - 5)) : (tpl('single') || (n >= 2 ? '11' + zeros(n - 2) : '1')));
        rows.push(mask + ': ' + tok(x) + (look ? ', [ten_' + x.nm + '%l]' : ''));
      }
    }
    const tray = $('toTray').checked;
    if (!tray) text = text.slice(0, vi.rowInsert) + rows.map((r) => '      <item value="' + r + '"/>\n').join('') + text.slice(vi.rowInsert);
    bcode.replaceAll(text);
    if (tray) { toast('Đã thêm các trường của khối "' + b.name + '" vào khay “Biến chưa dùng” — kéo từng trường vào form.'); return; }
    toast('Đã thêm khối "' + b.name + '"' + (cat ? ' vào tab mới' : ' ở cuối form') + ' — kéo ⋮⋮ để đổi chỗ, bấm đúp đổi nhãn, chuột phải xoá dòng.');
  }
  $('compGrid').addEventListener('click', (e) => { const el = e.target.closest('.blk:not(.click)'); if (el) addComposite(allComps().find((x) => x.id === el.dataset.id)).catch((er) => toast(er.message, true)); });
  $('compGrid').addEventListener('dragstart', (e) => {
    const el = e.target.closest('.blk:not(.click)'); if (!el) { e.preventDefault(); return; }
    if (!getText()) { e.preventDefault(); toast('Mở hoặc tạo 1 màn hình trước.', true); return; }
    const b = allComps().find((x) => x.id === el.dataset.id);
    preview._extDrag = { block: { comp: b }, name: '__block', kind: 'ctl', withLabel: true, size: preview.palSize };
    e.dataTransfer.effectAllowed = 'copy'; e.dataTransfer.setData('text/plain', 'bcode-field');
  });
  $('compGrid').addEventListener('dragend', () => { preview._extDrag = null; preview.clearDropMarks(); document.querySelectorAll('.dpNewRow').forEach((n) => n.classList.remove('dpDrop')); });

  // Xoá dòng thiết kế: chuột phải vào dòng trong form
  const reEsc = (v) => v.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  $('dpRoot').addEventListener('contextmenu', async (e) => {
    const tr = e.target.closest('tr[data-rid]'); if (!tr) return;
    const row = preview.rowById.get(+tr.dataset.rid); if (!row || !(row.editable || row.partial)) return;
    e.preventDefault();
    const v = await ask('Xoá dòng này khỏi thiết kế? Gõ OK để xác nhận (trường vẫn còn trong khay “Biến chưa dùng”).', 'OK'); if (String(v || '').trim().toUpperCase() !== 'OK') return;
    const text = getText();
    const vs = text.search(/<view\b[^>]*\bid\s*=\s*"Dir"[^>]*>/i); if (vs < 0) return;
    let k = 0; for (const r of preview.rowById.values()) if (r.id < row.id && (r.editable || r.partial) && r.value === row.value) k++;
    const re = new RegExp('[ \\t]*<item\\b[^>]*\\bvalue="' + reEsc(row.value) + '"[^>]*/>[ \\t]*\\r?\\n?', 'g');
    re.lastIndex = vs; let m, i = 0;
    while ((m = re.exec(text))) { if (i++ === k) { bcode.replaceAll(text.slice(0, m.index) + text.slice(m.index + m[0].length)); toast('Đã xoá dòng — Ctrl+Z để hoàn tác.'); return; } }
    toast('Không tìm thấy dòng trong source (có thể do entity).', true);
  });

  $('blockGrid').innerHTML = BLOCKS.map((b) => '<div class="blk' + (b.click ? ' click' : '') + '" draggable="' + (b.click ? 'false' : 'true') + '" data-id="' + b.id + '" title="' +
    (b.click ? 'Bấm để thêm' : 'Kéo vào form, hoặc bấm để thêm vào cuối') + '"><span class="ic">' + esc(b.ic) + '</span><span class="nm">' + esc(b.name) + '</span></div>').join('');

  // Khối kéo thả: tạo <field> mới NGAY LÚC THẢ (để kéo không bị vẽ lại giữa chừng), rồi giao cho logic chèn sẵn có của "Xem trước Dir".
  function materialize(ext) {
    const b = ext && ext.block; if (!b) return ext;
    const text = getText(), name = uniqueName(text, b.prefix);
    const r = insertField(text, { name, type: b.type, vn: b.vn, ctl: b.ctl }, false);
    bcode.replaceAll(r.text);
    return { name, kind: b.id === 'label' ? 'lbl' : 'ctl', withLabel: b.id !== 'label', size: preview.palSize };
  }
  const origInsertNew = preview.applyInsertNew.bind(preview), origNewRow = preview.applyNewRow.bind(preview);
  const isComp = (ext) => ext && ext.block && ext.block.comp;
  preview.applyInsertNew = (ext, td, x) => (isComp(ext) ? addComposite(ext.block.comp).catch((er) => toast(er.message, true)) : origInsertNew(materialize(ext), td, x));
  preview.applyNewRow = (ext) => (isComp(ext) ? addComposite(ext.block.comp).catch((er) => toast(er.message, true)) : origNewRow(materialize(ext)));

  $('blockGrid').addEventListener('dragstart', (e) => {
    const el = e.target.closest('.blk'); if (!el || el.classList.contains('click')) { e.preventDefault(); return; }
    if (!getText()) { e.preventDefault(); toast('Mở hoặc tạo 1 màn hình trước.', true); return; }
    const b = BLOCKS.find((x) => x.id === el.dataset.id);
    preview._extDrag = { block: b, name: '__block', kind: b.id === 'label' ? 'lbl' : 'ctl', withLabel: b.id !== 'label', size: preview.palSize };
    e.dataTransfer.effectAllowed = 'copy'; e.dataTransfer.setData('text/plain', 'bcode-field');
  });
  $('blockGrid').addEventListener('dragend', () => { preview._extDrag = null; preview.clearDropMarks(); document.querySelectorAll('.dpNewRow').forEach((n) => n.classList.remove('dpDrop')); });
  $('blockGrid').addEventListener('click', async (e) => {
    const el = e.target.closest('.blk'); if (!el) return;
    const b = BLOCKS.find((x) => x.id === el.dataset.id);
    const text = getText(); if (!text) { toast('Mở hoặc tạo 1 màn hình trước.', true); return; }
    if (b.click === 'tab') { $('bAddTab').click(); return; }
    if (b.click === 'grid') { openGridDlg(''); return; }
    try {
      let name = uniqueName(text, b.prefix), vn = b.vn;
      if ($('askName').checked) {
        const v = await ask('Nhãn / tên cho "' + b.name + '"', b.vn); if (v == null || !v.trim()) return;
        vn = v.trim();
        const base = slug(vn) || b.prefix; name = base; let i = 1;
        while (new RegExp('<field\\b[^>]*\\bname="' + name + '"').test(text)) name = base + (++i);
      }
      const r = insertField(text, { name, type: b.type, vn, ctl: b.ctl }, !$('toTray').checked);
      bcode.replaceAll(r.text);
      toast(r.placed ? 'Đã thêm "' + vn + '" ở cuối form — kéo để đổi chỗ, bấm đúp để đổi nhãn.' : 'Đã thêm "' + vn + '" vào khay “Biến chưa dùng” — kéo từ khay vào form.');
    } catch (err) { toast(err.message, true); }
  });

  // ------------------------------------------------------------------ lưới chi tiết: thiết kế cột
  S.grids = {};   // controller -> [{ name, label, width, numeric }]  (chỉ nằm trong thiết kế, không ghi vào source)
  const origLoadGrid = preview.loadGrid.bind(preview);
  preview.loadGrid = async (c) => (S.grids[c] ? { columns: S.grids[c], hidden: 0 } : origLoadGrid(c));
  const colsToText = (cols) => cols.map((c) => c.label + (c.numeric ? ', số' : '') + (c.width && c.width !== 80 ? (c.numeric ? '' : ', ') + ', ' + c.width : '')).join('\n').replace(/, ,/g, ',');
  function parseCols(t) {
    const out = [], used = new Set();
    for (const line of t.split(/\r?\n/)) {
      const p = line.split(/[,|;\t]/).map((x) => x.trim()); if (!p[0]) continue;
      let base = slug(p[0]) || 'cot', name = base, i = 1; while (used.has(name)) name = base + (++i); used.add(name);
      const num = p.slice(1).some((x) => /^(số|so|num|n)$/i.test(x)), w = p.slice(1).map((x) => parseInt(x, 10)).find((n) => n > 0);
      out.push({ name, label: p[0], width: w || (num ? 100 : 120), numeric: num });
    }
    return out;
  }
  function openGridDlg(ctl, cat) {
    if (!getText()) { toast('Mở hoặc tạo 1 màn hình trước.', true); return; }
    const vi = viewInfo(getText());
    $('gCat').innerHTML = '<option value="">(form chính)</option>' + ((vi && vi.cats) || []).filter((c) => c.index && c.index !== '-1').map((c) => '<option value="' + escAttr(c.index) + '">Tab ' + esc(c.index) + '</option>').join('');
    $('gCat').value = cat != null ? String(cat) : ((vi && vi.cats && vi.cats[0] && vi.cats[0].index) || '');
    $('gCtl').value = ctl || uniqueCtl();
    const cur = S.grids[ctl] || (preview.gridInfo && preview.gridInfo.get(ctl) && preview.gridInfo.get(ctl).columns);
    $('gCols').value = cur ? colsToText(cur) : 'Mã vật tư\nTên vật tư\nĐVT\nSố lượng, số\nĐơn giá, số\nThành tiền, số';
    $('gridOv').style.display = 'flex'; $('gCols').focus();
  }
  const uniqueCtl = () => { let n = 1; while (S.grids['LuoiChiTiet' + n]) n++; return 'LuoiChiTiet' + n; };
  $('gCancel').onclick = () => { $('gridOv').style.display = 'none'; };
  $('gOk').onclick = async () => {
    const ctl = $('gCtl').value.trim().replace(/[^\w]/g, ''), cols = parseCols($('gCols').value);
    if (!ctl) { toast('Nhập tên controller của lưới.', true); return; }
    if (!cols.length) { toast('Nhập ít nhất 1 cột.', true); return; }
    S.grids[ctl] = cols; $('gridOv').style.display = 'none';
    let text = getText();
    if (!new RegExp('<items\\b[^>]*style="Grid"[^>]*controller="' + ctl + '"').test(text)) {
      try {
        const name = uniqueName(text, 'd'), cat = $('gCat').value;
        text = insertField(text, { name, type: 'grid', vn: '', ctl, cat }, false).text;
        const vi = viewInfo(text); if (vi) text = text.slice(0, vi.rowInsert) + '      <item value="1: [' + name + ']"/>\n' + text.slice(vi.rowInsert);
        bcode.replaceAll(text);
      } catch (err) { toast(err.message, true); return; }
    } else await preview.render(true);
    toast('Đã thiết kế lưới ' + ctl + ' (' + cols.length + ' cột). Bấm đúp vào lưới để sửa cột.');
  };

  // Bấm đúp vào nhãn / ô trong form = đổi nhãn tiếng Việt của trường đó
  $('dpRoot').addEventListener('dblclick', async (e) => {
    const g = e.target.closest('.dpGrid');
    if (g) {
      const f = g.dataset.field || ''; const m = new RegExp('<field\\b[^>]*\\bname="' + f + '"[^>]*>[\\s\\S]*?controller="([^"]+)"').exec(getText());
      if (m) { openGridDlg(m[1]); return; }
    }
    const el = e.target.closest('[data-field]'); if (!el) return;
    const name = el.dataset.field.replace(/\.Label$/, '');
    const text = getText();
    const re = new RegExp('(<field\\b[^>]*\\bname="' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '"[^>]*>\\s*<header\\b[^>]*\\bv=")([^"]*)(")');
    const m = re.exec(text); if (!m) { toast('Không tìm thấy khai báo nhãn của "' + name + '" trong file (có thể nằm ở entity).', true); return; }
    const v = await ask('Nhãn của [' + name + ']', decode(m[2])); if (v == null) return;
    bcode.replaceAll(text.replace(re, (_, a, _b, c) => a + encode(v) + c));
  });

  // ------------------------------------------------------------------ tab trái
  document.querySelectorAll('#ltabs .lt').forEach((b) => b.addEventListener('click', () => {
    document.querySelectorAll('#ltabs .lt').forEach((x) => x.classList.toggle('on', x === b));
    $('paneScreens').style.display = b.dataset.p === 'screens' ? 'flex' : 'none';
    $('paneBlocks').style.display = b.dataset.p === 'blocks' ? 'block' : 'none';
    $('paneMine').style.display = b.dataset.p === 'mine' ? 'flex' : 'none';
    if (b.dataset.p === 'mine') refreshMine();
  }));

  // ------------------------------------------------------------------ "Nạp sẵn" source về máy
  let mirrorTimer = null;
  async function refreshMirror() {
    clearTimeout(mirrorTimer);
    let m; try { m = JSON.parse(await call('BeginMirrorInfo', S.source)); } catch { return; }
    S.mirror = m;
    if (m.running) {
      $('mirrorInfo').textContent = 'Đang nạp: ' + m.done + ' / ' + m.total + ' file…';
      $('bMirror').disabled = true;
      mirrorTimer = setTimeout(refreshMirror, 1000);
      return;
    }
    $('bMirror').disabled = false;
    $('mirrorInfo').textContent = m.error ? 'Lỗi nạp: ' + m.error : (m.exists ? 'Đã chép ' + m.files + ' file mẫu lúc ' + m.stamp + ' — file khác tự chép về máy khi mở lần đầu.' : 'Chưa chép mẫu — file sẽ tự chép về máy khi mở lần đầu.');
    if (S.mirrorWasRunning) {
      S.mirrorWasRunning = false;
      if (m.exists && !m.error) { $('useMirror').checked = true; S.useMirror = true; await applyCache(); saveState(); toast('Đã nạp xong mẫu chuẩn về máy.'); }
    }
  }
  $('bMirror').onclick = async (e) => {
    if (!S.source) { toast('Chọn source trước.', true); return; }
    try {
      const full = e.shiftKey || (await ask('Nạp nhanh mẫu chọn lọc (vài chục file) — gõ FULL nếu muốn chép TOÀN BỘ source chuẩn (nhiều nghìn file, lâu):', 'NHANH') || '').trim().toUpperCase() === 'FULL';
      await call('BeginMirrorStart', S.source, full ? 'full' : ''); S.mirrorWasRunning = true; refreshMirror();
    } catch (er) { toast(er.message || String(er), true); }
  };
  $('useMirror').onchange = async () => { S.useMirror = $('useMirror').checked; await applyCache(); saveState(); };
  $('bRescan').onclick = () => loadScreens(true);

  // ================================================================ chọn ô + Delete / đổi tên biến / dữ liệu test / xoá tab / Mẫu của tôi
  S.tests = S.state.tests || {};   // đường dẫn màn hình -> { tên biến: giá trị test }
  S.test = {}; S.showTest = true; S.chip = '';
  const varOf = (c) => { const m = /^\[([^\]]+)\]/.exec((c && (c.token || c.raw)) || ''); return m ? m[1] : ''; };
  const demo = (t) => String(t || '').replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));

  // ---- menu chuột phải nhỏ
  function closeMenu() { const m = $('ctxm'); if (m) m.remove(); }
  function showMenu(x, y, items) {
    closeMenu();
    const m = document.createElement('div'); m.id = 'ctxm';
    items.forEach((it) => {
      if (it === '-') { const s = document.createElement('div'); s.className = 'sp'; m.appendChild(s); return; }
      const d = document.createElement('div'); d.className = 'mi' + (it.danger ? ' danger' : '') + (it.off ? ' off' : '');
      d.innerHTML = '<span>' + esc(it.label) + '</span>' + (it.key ? '<kbd>' + esc(it.key) + '</kbd>' : '');
      if (!it.off) d.onclick = () => { closeMenu(); it.fn(); };
      m.appendChild(d);
    });
    document.body.appendChild(m);
    const w = m.offsetWidth, h = m.offsetHeight;
    m.style.left = Math.max(4, Math.min(x, innerWidth - w - 4)) + 'px'; m.style.top = Math.max(4, Math.min(y, innerHeight - h - 4)) + 'px';
  }
  document.addEventListener('mousedown', (e) => { if (!e.target.closest('#ctxm')) closeMenu(); }, true);
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeMenu(); });

  // ---- chọn ô (bấm = chọn 1 ô; Ctrl+bấm = chọn nhiều ô cùng dòng — logic có sẵn của Xem trước Dir)
  const cellKey = (td) => td.dataset.r + ':' + td.dataset.c;
  function setSel(tds) {
    preview.sel = new Set(tds.map(cellKey));
    $('dpRoot').querySelectorAll('td.dpTd').forEach((t) => t.classList.toggle('dpSel', preview.sel.has(cellKey(t))));
    $('dpRoot').querySelectorAll('.dpChip.pick').forEach((c) => c.classList.remove('pick'));
    S.chip = '';
  }
  $('dpRoot').addEventListener('click', (e) => {
    if (e.ctrlKey || e.metaKey || e.shiftKey) return;
    const chip = e.target.closest('.dpChip');
    if (chip) { setSel([]); S.chip = chip.dataset.name; chip.classList.add('pick'); status('Đã chọn biến "' + S.chip + '" trong khay — nhấn Delete để xoá hẳn khỏi thiết kế.'); return; }
    const td = e.target.closest('td.dpTd');
    if (td) { setSel([td]); const n = fieldOf(td); status(n ? 'Đã chọn [' + n + '] — nhấn Delete để xoá khỏi form, Shift+Delete để xoá hẳn biến.' : ''); }
    else if (!e.target.closest('.dpTab,.dpPalette')) setSel([]);
  }, true);

  const fieldOf = (td) => { const el = td.querySelector('[data-field]'); return el ? el.dataset.field : ''; };

  // ---- sửa văn bản theo dòng thiết kế
  function spanOf(text, row) {
    const vs = text.search(/<view\b[^>]*\bid\s*=\s*"Dir"[^>]*>/i); if (vs < 0) return null;
    let k = 0; for (const r of preview.rowById.values()) if (r.id < row.id && (r.editable || r.partial) && r.value === row.value) k++;
    const needle = 'value="' + row.value + '"';
    let at = vs - 1;
    for (let i = 0; i <= k; i++) { at = text.indexOf(needle, at + 1); if (at < 0) return null; }
    const open = text.lastIndexOf('<item', at), gt = text.indexOf('>', at + needle.length);
    if (open < 0 || gt < 0) return null;
    return { valStart: at + 7, valEnd: at + 7 + row.value.length, open, close: gt + 1 };
  }
  function applyOps(text, ops) {
    const edits = [];
    for (const op of ops) {
      const s = spanOf(text, op.row); if (!s) return null;
      if (op.remove) {
        let a = s.open; while (a > 0 && /[ \t]/.test(text[a - 1])) a--;
        let b = s.close; while (b < text.length && /[ \t]/.test(text[b])) b++;
        if (text[b] === '\r') b++; if (text[b] === '\n') b++;
        edits.push({ a, b, t: '' });
      } else edits.push({ a: s.valStart, b: s.valEnd, t: op.value });
    }
    edits.sort((x, y) => y.a - x.a);
    for (const e of edits) text = text.slice(0, e.a) + e.t + text.slice(e.b);
    return text;
  }
  /// Kế hoạch bỏ các ô thoả pred(row, chỉ số ô, ô): dòng còn ô thì ghi lại mặt nạ mới, hết ô thì xoá cả dòng.
  function planRemoval(pred) {
    const ops = []; let skipped = 0;
    for (const row of preview.rowById.values()) {
      const rem = row.cells.map((c, i) => !!pred(row, i, c)); if (!rem.some(Boolean)) continue;
      if (!row.editable) { skipped++; continue; }
      const keep = row.cells.filter((c, i) => !rem[i]).map((c) => Object.assign({}, c));
      if (!keep.length) { ops.push({ row, remove: true }); continue; }
      const v = preview.buildRowValue({ row, mask: row.mask, cells: keep });
      if (v == null) { skipped++; continue; }
      ops.push({ row, value: v });
    }
    return { ops, skipped };
  }
  function removeFieldDecl(text, name) {
    const m = new RegExp('<field\\b[^>]*\\bname="' + reEsc(name) + '"', 'i').exec(text); if (!m) return text;
    const gt = text.indexOf('>', m.index); if (gt < 0) return text;
    let end = gt + 1;
    if (text[gt - 1] !== '/') { const c = text.indexOf('</field>', gt); if (c < 0) return text; end = c + 8; }
    let a = m.index; while (a > 0 && /[ \t]/.test(text[a - 1])) a--;
    let b = end; while (b < text.length && /[ \t]/.test(text[b])) b++;
    if (text[b] === '\r') b++; if (text[b] === '\n') b++;
    return text.slice(0, a) + text.slice(b);
  }
  const withNameField = (names) => { const s = new Set(names); names.forEach((n) => s.add('ten_' + n + '%l')); return s; };

  /// Xoá các biến khỏi thiết kế: bỏ mọi ô dùng chúng ở tất cả các dòng + xoá khai báo <field>.
  function deleteVariables(names) {
    const set = withNameField(names);
    const { ops, skipped } = planRemoval((row, i, c) => set.has(varOf(c)));
    let text = applyOps(getText(), ops); if (text == null) { toast('Không tìm thấy dòng trong source (có thể do entity).', true); return; }
    set.forEach((n) => { text = removeFieldDecl(text, n); });
    bcode.replaceAll(text);
    toast('Đã xoá hẳn biến ' + names.join(', ') + ' (Ctrl+Z để hoàn tác).' + (skipped ? ' ' + skipped + ' dòng có entity chưa sửa được.' : ''));
  }
  /// Xoá các ô đang chọn khỏi form (biến vẫn còn trong khay “Biến chưa dùng”); hard = xoá hẳn biến.
  function deleteSelection(hard) {
    if (S.chip && !(preview.sel && preview.sel.size)) { deleteVariables([S.chip]); S.chip = ''; return; }
    const sel = [...(preview.sel || [])];
    if (!sel.length) { toast('Chưa chọn ô nào — bấm vào nhãn / ô trong form (Ctrl+bấm chọn nhiều ô cùng dòng) rồi nhấn Delete.', true); return; }
    if (hard) {
      const names = new Set();
      for (const k of sel) { const [r, c] = k.split(':').map(Number); const row = preview.rowById.get(r); const n = row && row.cells[c] ? varOf(row.cells[c]) : ''; if (n) names.add(n); }
      if (names.size) { deleteVariables([...names]); return; }
    }
    const chosen = new Set(sel);
    const { ops, skipped } = planRemoval((row, i) => chosen.has(row.id + ':' + i));
    if (!ops.length) { toast(skipped ? 'Ô này nằm trong dòng có entity (&...;) nên chưa xoá tự động được — sửa ở “Mã XML”.' : 'Không có gì để xoá.', true); return; }
    const text = applyOps(getText(), ops); if (text == null) { toast('Không tìm thấy dòng trong source (có thể do entity).', true); return; }
    bcode.replaceAll(text);
    toast('Đã xoá ' + sel.length + ' ô khỏi form — biến còn trong khay “Biến chưa dùng”; Ctrl+Z để hoàn tác.');
  }
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Delete' || (e.target && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName))) return;
    e.preventDefault(); deleteSelection(e.shiftKey);
  });

  // ---- đổi tên biến (cả nhãn / ô / khai báo / tên bắc cầu ten_x%l)
  function renameInText(text, a, b) {
    for (const [x, y] of [[a, b], ['ten_' + a + '%l', 'ten_' + b + '%l']]) {
      const q = reEsc(x);
      text = text.replace(new RegExp('(<field\\b[^>]*\\bname=")' + q + '(")', 'g'), (_m, p, s) => p + y + s)
                 .replace(new RegExp('\\[' + q + '\\]', 'g'), () => '[' + y + ']')
                 .replace(new RegExp('(reference=")' + q + '(")', 'g'), (_m, p, s) => p + y + s);
    }
    return text;
  }
  async function renameVar(old) {
    const text = getText();
    if (!new RegExp('<field\\b[^>]*\\bname="' + reEsc(old) + '"').test(text)) { toast('Biến "' + old + '" khai báo trong file entity (&...;) nên không đổi tên ở đây được.', true); return; }
    const v = await ask('Tên biến mới cho [' + old + '] (không dấu, vd ' + old + '1)', old); if (v == null) return;
    const nn = v.trim(); if (!nn || nn === old) return;
    if (!/^[A-Za-z_][\w$]*$/.test(nn)) { toast('Tên biến chỉ gồm chữ không dấu, số và _ (không bắt đầu bằng số).', true); return; }
    if (new RegExp('<field\\b[^>]*\\bname="' + reEsc(nn) + '"').test(text)) { toast('Đã có biến tên "' + nn + '".', true); return; }
    bcode.replaceAll(renameInText(text, old, nn));
    if (S.test[old] !== undefined) { S.test[nn] = S.test[old]; delete S.test[old]; keepTest(); }
    toast('Đã đổi biến ' + old + ' → ' + nn + (/ten_/.test(old) ? '' : ' (kèm ten_' + old + '%l nếu có).') + ' Ctrl+Z để hoàn tác.');
  }
  async function editLabel(name) {
    const text = getText();
    const re = new RegExp('(<field\\b[^>]*\\bname="' + reEsc(name) + '"[^>]*>\\s*<header\\b[^>]*\\bv=")([^"]*)(")');
    const m = re.exec(text); if (!m) { toast('Không tìm thấy khai báo nhãn của "' + name + '" trong file (có thể nằm ở entity).', true); return; }
    const v = await ask('Nhãn của [' + name + ']', decode(m[2])); if (v == null) return;
    bcode.replaceAll(text.replace(re, (_, a, _b, c) => a + encode(v) + c));
  }

  // ---- dữ liệu test (chỉ nằm trong thiết kế + ảnh chụp, không ghi vào source)
  function keepTest() {
    if (S.current && S.current.p) { if (Object.keys(S.test).length) S.tests[S.current.p] = S.test; else delete S.tests[S.current.p]; const ks = Object.keys(S.tests); if (ks.length > 40) delete S.tests[ks[0]]; }
    saveState(); applyTest();
  }
  function applyTest() {
    $('dpRoot').querySelectorAll('.dpFC[data-field]').forEach((fc) => {
      fc.querySelectorAll('.dpTest').forEach((n) => n.remove()); fc.classList.remove('hasTest');
      const chk0 = fc.querySelector('.dpChk'); if (chk0) { chk0.textContent = ''; chk0.classList.remove('on'); }
      const v = S.test[fc.dataset.field];
      if (v === undefined || v === '' || !S.showTest) return;
      const chk = fc.querySelector('.dpChk');
      if (chk) { chk.textContent = /^(1|true|x|co|có|yes|y)$/i.test(String(v).trim()) ? '✓' : ''; chk.classList.add('on'); fc.classList.add('hasTest'); return; }
      const box = fc.querySelector('.dpBox'); if (!box) return;
      const num = box.querySelector('.dpNum');
      if (num) num.textContent = v; else { const s = document.createElement('span'); s.className = 'dpTest'; s.textContent = v; box.insertBefore(s, box.firstChild); }
      fc.classList.add('hasTest');
    });
    // Lưới chi tiết: ô (dòng r, cột col) lưu ở S.test["<biến lưới>#r#col"]
    $('dpRoot').querySelectorAll('.dpGridReal[data-field]').forEach((g) => {
      const gv = g.dataset.field;
      const cols = [...g.querySelectorAll('thead th')].map((th) => th.title);
      g.querySelectorAll('tbody tr').forEach((tr, r) => {
        [...tr.children].forEach((td, c) => {
          if (c === 0) return;
          const v = S.showTest ? S.test[gv + '#' + r + '#' + cols[c]] : undefined;
          td.textContent = v === undefined ? '' : v;
        });
      });
    });
  }
  const origDraw = preview.draw.bind(preview);
  preview.draw = function (a) { const r = origDraw(a); applyTest(); return r; };
  async function editTest(name) {
    const fc = [...$('dpRoot').querySelectorAll('.dpFC[data-field]')].find((x) => x.dataset.field === name);
    if (fc && fc.querySelector('.dpChk')) { S.test[name] = /^(1|true|x)$/i.test(String(S.test[name] || '')) ? '0' : '1'; keepTest(); return; }
    const v = await ask('Dữ liệu test cho [' + name + '] (để trống = xoá)', S.test[name] || ''); if (v == null) return;
    if (v === '') delete S.test[name]; else S.test[name] = v;
    keepTest();
  }
  async function editGridCell(td) {
    const g = td.closest('.dpGridReal'), gv = g.dataset.field;
    const ths = [...g.querySelectorAll('thead th')], c = [...td.parentNode.children].indexOf(td);
    const r = [...td.closest('tbody').children].indexOf(td.parentNode);
    const key = gv + '#' + r + '#' + ths[c].title;
    const v = await ask('Dữ liệu test — cột “' + ths[c].textContent + '”, dòng ' + (r + 1) + ' (để trống = xoá)', S.test[key] || ''); if (v == null) return;
    if (v === '') delete S.test[key]; else S.test[key] = v;
    keepTest();
  }
  $('dpRoot').addEventListener('dblclick', (e) => {
    const gc = e.target.closest('.dpGridReal tbody td');
    if (gc && !gc.classList.contains('dpGi')) { e.stopImmediatePropagation(); e.preventDefault(); editGridCell(gc); return; }
    if (e.target.closest('.dpGrid') || e.target.closest('.dpLbl')) return;
    const fc = e.target.closest('.dpFC[data-field]'); if (!fc) return;
    e.stopImmediatePropagation(); e.preventDefault();
    editTest(fc.dataset.field);
  }, true);
  function sampleOf(name, fc) {
    const n = name.toLowerCase();
    if (fc.querySelector('.dpChk')) return '1';
    if (fc.querySelector('.dpBox.num')) return /ty_gia|rate/.test(n) ? '1.0000' : /so_luong|^sl|qty/.test(n) ? '10' : /thue_suat|ts_/.test(n) ? '10' : '15,000,000';
    if (fc.querySelector('.cal')) return new Date().toLocaleDateString('vi-VN');
    if (/^ten_.*%l$/.test(n)) return /nt|tien/.test(n) ? 'Đồng Việt Nam' : /kho/.test(n) ? 'Kho thành phẩm' : /vt/.test(n) ? 'Vật tư mẫu' : 'Công ty TNHH ABC';
    if (/^ma_nt$/.test(n)) return 'VND';
    if (/^ma_kh/.test(n)) return 'KH001';
    if (/^ma_thue/.test(n)) return '10';
    if (/^ma_vt/.test(n)) return 'VT001';
    if (/^ma_/.test(n)) return 'M001';
    if (/so_ct|so_hd|^so_/.test(n)) return 'HD0001';
    if (/dia_chi/.test(n)) return '12 Nguyễn Huệ, Quận 1, TP. Hồ Chí Minh';
    if (/dien_giai|dien_giải/.test(n)) return 'Bán hàng theo hợp đồng số 01/2026';
    if (/dien_thoai|^tel|fax/.test(n)) return '0901 234 567';
    if (/mail/.test(n)) return 'abc@congty.vn';
    if (/ma_so_thue|mst/.test(n)) return '0301234567';
    if (/^ten_|^ong_ba/.test(n)) return 'Nguyễn Văn A';
    if (/ghi_chu/.test(n)) return 'Ghi chú mẫu';
    if (fc.querySelector('.look')) return 'M001';
    return 'Nội dung mẫu';
  }
  function autoFillTest() {
    $('dpRoot').querySelectorAll('.dpFC[data-field]').forEach((fc) => { const nm = fc.dataset.field; if (S.test[nm] === undefined) S.test[nm] = sampleOf(nm, fc); });
    const fmt = (n) => Number(n).toLocaleString('en-US');
    $('dpRoot').querySelectorAll('.dpGridReal[data-field]').forEach((g) => {
      const gv = g.dataset.field, ths = [...g.querySelectorAll('thead th')];
      const nRows = Math.min(3, g.querySelectorAll('tbody tr').length);
      for (let r = 0; r < nRows; r++) for (let c = 1; c < ths.length; c++) {
        const k = gv + '#' + r + '#' + ths[c].title; if (S.test[k] !== undefined) continue;
        const isNum = !!g.querySelector('tbody tr td.num:nth-child(' + (c + 1) + ')');
        const s = (ths[c].title + ' ' + ths[c].textContent).toLowerCase();
        const qty = [10, 25, 8][r], price = [150000, 82000, 1250000][r];
        let v = '';
        if (isNum) {
          if (/ton|tồn/.test(s)) v = String([120, 340, 56][r]);
          else if (/so_luong|số lượng|\bsl\b|qty/.test(s)) v = String(qty);
          else if (/thue_suat|tỷ lệ|ty_le|%/.test(s)) v = '10';
          else if (/von|vốn/.test(s)) v = fmt(qty * price * 0.8);
          else if (/don_gia|đơn giá|giá|gia_/.test(s)) v = fmt(price);
          else if (/chiet|chiết|km|khuyến/.test(s)) v = '';
          else if (/thue|thuế/.test(s)) v = fmt(qty * price * 0.1);
          else if (/tien|tiền/.test(s)) v = fmt(qty * price);   // cột số khác (tồn, loại, tỷ lệ…) để trống cho người dùng tự nhập
        } else if (/ma_vt|mã hàng|mã vật tư|ma_hang/.test(s)) v = ['VT001', 'VT002', 'VT003'][r];
        else if (/ten_vt|tên mặt hàng|tên vật tư|ten_hang|ten_mat/.test(s)) v = ['Hạt điều nhân W320', 'Hạt điều rang muối', 'Bao bì carton 20kg'][r];
        else if (/dvt|đvt/.test(s)) v = ['Kg', 'Kg', 'Thùng'][r];
        else if (/ma_kho|mã kho/.test(s)) v = 'KHO1';
        else if (/ma_thue|mã thuế/.test(s)) v = '10';
        else if (/\btk|tài khoản/.test(s)) v = /doanh thu/.test(s) ? '5111' : /chiết|chiet/.test(s) ? '5211' : /thuế|thue/.test(s) ? '33311' : /vốn|von/.test(s) ? '632' : /kho/.test(s) ? '1561' : '';
        else if (/lô|ma_lo/.test(s)) v = 'L2610' + (r + 1);
        if (v !== '') S.test[k] = v;
      }
    });
    S.showTest = true; keepTest(); toast('Đã điền dữ liệu mẫu (kể cả lưới chi tiết) — bấm đúp vào từng ô để sửa; chụp ảnh sẽ kèm dữ liệu này.');
  }
  $('bTest').onclick = (e) => {
    const r = e.currentTarget.getBoundingClientRect();
    showMenu(r.left, r.bottom + 2, [
      { label: 'Điền dữ liệu mẫu tự động', fn: autoFillTest },
      { label: S.showTest ? 'Ẩn dữ liệu test (về ô trắng + tên biến)' : 'Hiện dữ liệu test', fn: () => { S.showTest = !S.showTest; applyTest(); } },
      '-',
      { label: 'Xoá toàn bộ dữ liệu test', danger: true, off: !Object.keys(S.test).length, fn: () => { S.test = {}; keepTest(); } },
      { label: 'Mẹo: bấm đúp vào ô để nhập dữ liệu cho ô đó', off: true },
    ]);
  };

  // ---- xoá / đổi tên tab (tab = <category>; dòng của tab = dòng có trường categoryIndex tương ứng)
  function tabLabel(idx) { const el = [...$('dpRoot').querySelectorAll('.dpTab')].find((t) => t.dataset.tab === String(idx)); return el ? el.textContent : 'Tab ' + idx; }
  async function removeTab(idx) {
    if (idx == null) { toast('Không có tab nào đang chọn.', true); return; }
    const label = tabLabel(idx);
    const v = await ask('Xoá tab "' + label + '" cùng các dòng trong tab? Gõ OK để xác nhận (Ctrl+Z hoàn tác được).', 'OK'); if (String(v || '').trim().toUpperCase() !== 'OK') return;
    const { ops, skipped } = planRemoval((row) => String(row.cat) === String(idx));
    let text = applyOps(getText(), ops.map((o) => ({ row: o.row, remove: true }))); if (text == null) text = getText();
    const re = new RegExp('[ \\t]*<category\\b[^>]*\\bindex="' + reEsc(String(idx)) + '"[^>]*>', 'i'); const m = re.exec(text);
    if (!m) { toast('Tab này không khai báo trong <categories> của file (do entity) — sửa ở “Mã XML”.', true); return; }
    let end = m.index + m[0].length;
    if (!/\/>\s*$/.test(m[0])) { const c = text.indexOf('</category>', end); if (c < 0) { toast('Thiếu </category>.', true); return; } end = c + 11; }
    while (end < text.length && /[ \t]/.test(text[end])) end++;
    if (text[end] === '\r') end++; if (text[end] === '\n') end++;
    text = text.slice(0, m.index) + text.slice(end);
    if (!/<category\b/i.test(text)) text = text.replace(/[ \t]*<categories\b[^>]*>\s*<\/categories>[ \t]*\r?\n?/i, '');
    text = text.replace(/(<field\b[^>]*?)\s+categoryIndex="[^"]*"/g, (all, p) => (new RegExp('categoryIndex="' + reEsc(String(idx)) + '"').test(all) ? p : all));
    preview.activeTab = null;
    bcode.replaceAll(text);
    toast('Đã xoá tab "' + label + '" và ' + ops.length + ' dòng của tab' + (skipped ? ' (' + skipped + ' dòng entity chưa xoá được)' : '') + ' — các trường của tab còn trong khay “Biến chưa dùng”.');
  }
  async function renameTab(idx) {
    const text = getText();
    const re = new RegExp('(<category\\b[^>]*\\bindex="' + reEsc(String(idx)) + '"[^>]*>\\s*<header\\b[^>]*\\bv=")([^"]*)(")', 'i'); const m = re.exec(text);
    if (!m) { toast('Không tìm thấy khai báo tên tab này trong file.', true); return; }
    const v = await ask('Tên tab', decode(m[2])); if (v == null || !v.trim()) return;
    bcode.replaceAll(text.replace(re, (_, a, _b, c) => a + encode(v.trim()) + c));
  }
  $('bDelTab').onclick = () => removeTab(preview.activeTab);

  // ---- menu chuột phải: ô / tab / biến trong khay (chuột phải vào chỗ khác vẫn là “xoá dòng” như cũ)
  $('dpRoot').addEventListener('contextmenu', (e) => {
    const tab = e.target.closest('.dpTab'), td = e.target.closest('td.dpTd'), chip = e.target.closest('.dpChip');
    if (!tab && !td && !chip) return;
    e.preventDefault(); e.stopImmediatePropagation();
    if (tab) {
      const idx = tab.dataset.tab;
      showMenu(e.clientX, e.clientY, [{ label: 'Đổi tên tab…', fn: () => renameTab(idx) }, { label: 'Thêm tab…', fn: () => $('bAddTab').click() }, '-', { label: 'Xoá tab này (cùng các dòng của tab)', danger: true, fn: () => removeTab(idx) }]);
      return;
    }
    if (chip) {
      const nm = chip.dataset.name;
      showMenu(e.clientX, e.clientY, [{ label: 'Đổi tên biến…', fn: () => renameVar(nm) }, '-', { label: 'Xoá hẳn biến "' + nm + '"', danger: true, key: 'Delete', fn: () => deleteVariables([nm]) }]);
      return;
    }
    if (!(preview.sel && preview.sel.has(cellKey(td)))) setSel([td]);
    const nm = fieldOf(td), row = preview.rowById.get(+td.dataset.r);
    const isCtl = !!td.querySelector('.dpFC');
    const nmReal = nm.replace(/\.Label$/, '');
    showMenu(e.clientX, e.clientY, [
      { label: 'Nhập dữ liệu test…', off: !isCtl || !nm, fn: () => editTest(nmReal) },
      { label: 'Đổi nhãn…', off: !nm, fn: () => editLabel(nmReal) },
      { label: 'Đổi tên biến…', off: !nm, fn: () => renameVar(nmReal) },
      '-',
      { label: 'Xoá khỏi form (biến còn trong khay)', key: 'Delete', fn: () => deleteSelection(false) },
      { label: 'Xoá hẳn biến', danger: true, off: !nm, key: 'Shift+Delete', fn: () => deleteSelection(true) },
      { label: 'Xoá cả dòng này', danger: true, off: !row || !row.editable, fn: () => { const t = applyOps(getText(), [{ row, remove: true }]); if (t != null) { bcode.replaceAll(t); toast('Đã xoá dòng — Ctrl+Z để hoàn tác.'); } } },
    ]);
  }, true);

  // ---- Mẫu của tôi: lưu trên máy (%AppData%\BcodeScreenDesigner\my) + mở lại
  S.mine = [];
  async function refreshMineDir() {
    try { const i = JSON.parse(await call('BeginMineInfo')); S.mineDir = i.dir; $('mineDir').textContent = i.dir + (i.isDefault ? '  (mặc định)' : ''); $('bMineDef').style.display = i.isDefault ? 'none' : ''; } catch { /* host cũ */ }
  }
  $('bMineOpen').onclick = () => call('BeginOpenFolder', S.mineDir || '').catch((e) => toast(e.message || String(e), true));
  $('bMineDir').onclick = async () => {
    const p = await call('BeginPickFolder', S.mineDir || ''); if (!p) return;
    try { S.state.mineDir = await call('BeginSetMineDir', p); } catch (e) { toast(e.message || String(e), true); return; }
    saveState(); await refreshMineDir(); refreshMine(); toast('Mẫu mới sẽ lưu vào: ' + S.mineDir + ' (mẫu ở thư mục trước vẫn còn nguyên, không tự chuyển).');
  };
  $('bMineDef').onclick = async () => { await call('BeginSetMineDir', ''); S.state.mineDir = ''; saveState(); await refreshMineDir(); refreshMine(); };
  async function refreshMine() {
    refreshMineDir();
    let r = []; try { r = JSON.parse(await call('BeginListMine')); } catch { /* chưa có */ }
    S.mine = r; $('mineCnt').textContent = '(' + r.length + ')';
    S.mineByFrom = {}; r.forEach((m) => { if (m.from && !S.mineByFrom[m.from]) S.mineByFrom[m.from] = m.name; });   // r đã xếp mới nhất trước
    renderList();
    $('mineList').innerHTML = r.length
      ? r.map((m, i) => '<div class="it" data-i="' + i + '"><span class="n">' + esc(m.name) + '<button class="x" data-del="' + i + '" title="Xoá mẫu này">✕</button></span><span class="t">' + esc(m.time) + '</span></div>').join('')
      : '<div class="empty">Chưa có mẫu nào. Thiết kế xong bấm “💾 Lưu thiết kế” để lưu mẫu vào máy.</div>';
  }
  /// quick = true (Ctrl+S): đã có tên mẫu thì ghi đè luôn, không hỏi; lần đầu / Ctrl+Shift+S thì hỏi tên.
  async function saveMine(quick) {
    const t = getText(); if (!t) { toast('Chưa có thiết kế.', true); return; }
    const cur = S.current || {};
    const def = cur.mine || cur.n || 'Mẫu mới';
    let name = def;
    if (!(quick === true && cur.mine)) { name = await ask('Tên mẫu lưu trên máy (trùng tên = ghi đè)', def); if (!name || !name.trim()) return; }
    // màn hình chuẩn gốc (loại:tên) — lần sau mở lại đúng màn hình đó sẽ ra bản bạn đã sửa
    const from = cur.from || (!cur.isNew && cur.k ? cur.k + ':' + cur.n : '');
    try {
      const saved = await call('BeginSaveMine', name.trim(), t, JSON.stringify({ test: S.test, grids: S.grids, from }));
      if (S.current) { S.current.mine = saved; if (from) S.current.from = from; }
      await refreshMineDir(); toast('Đã lưu mẫu “' + saved + '” tại ' + S.mineDir + ' (file ' + saved + '.f) — mở lại ở tab “Mẫu của tôi”.'); refreshMine();
    } catch (e) { toast(e.message || String(e), true); }
  }
  async function openMine(name) {
    let r; try { r = JSON.parse(await call('BeginReadMine', name)); } catch (e) { toast('Không mở được mẫu: ' + (e.message || e), true); return; }
    loadDesign(name, r.text, r.meta || {}, name); status('Mẫu của tôi — ' + name);
  }
  function loadDesign(name, text, meta, mineName) {
    const keepFrom = (meta && meta.from) || '';
    const dir = (S.root || S.source) ? (S.root || S.source).replace(/[\\/]+$/, '') + '\\App_Data\\Controllers\\Dir' : 'C:\\Designer';
    S.current = { k: 'Dir', n: name, p: dir + '\\_mine_' + slug(name) + '.f', isNew: true, mine: mineName || '', from: /^[A-Za-z]+:/.test(keepFrom) ? keepFrom : '' };
    $('bOrig').style.display = S.current.from ? '' : 'none';
    S.grids = meta.grids || {}; S.test = meta.test || {};
    bcode.setText(String(text || '').replace(/^\uFEFF/, ''), S.current.p);
    syncAll(); renderList(); saveState();
  }
  // ---- Gói thiết kế (.bcdesign): xuất ra file để copy sang máy khác, nhập lại để sửa tiếp
  $('bPackOut').onclick = async () => {
    const t = getText(); if (!t) { toast('Chưa có thiết kế.', true); return; }
    const name = (S.current && (S.current.mine || S.current.n)) || 'thiet-ke';
    const pack = { format: 'bcode-screen-design', version: 1, name, savedAt: new Date().toISOString(), text: t, meta: { test: S.test, grids: S.grids, from: S.current ? S.current.n : '' } };
    try {
      const p = await call('BeginSaveText', fileBase() + '.bcdesign', 'Gói thiết kế Bcode (*.bcdesign)|*.bcdesign', JSON.stringify(pack, null, 1));
      if (p) { S.state.lastPack = p; saveState(); toast('Đã xuất gói thiết kế: ' + p + ' — copy sang máy khác rồi bấm “📥 Nhập gói…” để sửa tiếp.'); }
    } catch (e) { toast(e.message || String(e), true); }
  };
  $('bPackIn').onclick = async () => {
    const last = S.state.lastPack ? S.state.lastPack.replace(/[\\/][^\\/]*$/, '') : '';
    let p; try { p = await call('BeginPickFile', 'Gói thiết kế Bcode (*.bcdesign)|*.bcdesign|Tất cả file|*.*', last); } catch (e) { toast(e.message || String(e), true); return; }
    if (!p) return;
    let pack; try { pack = JSON.parse(String(await call('BeginReadFile', p) || '').replace(/^\uFEFF/, '')); } catch (e) { toast('Không đọc được gói: ' + (e.message || e), true); return; }
    if (!pack || pack.format !== 'bcode-screen-design' || !pack.text) { toast('File này không phải gói thiết kế của Bcode Screen Designer.', true); return; }
    loadDesign(pack.name || 'Gói nhập', pack.text, pack.meta || {}, '');
    S.state.lastPack = p; saveState(); status('Gói thiết kế — ' + (pack.name || ''));
    toast('Đã nhập gói “' + (pack.name || '') + '” từ ' + p + ' — sửa tiếp, rồi 💾 Lưu vào “Mẫu của tôi” nếu muốn giữ trên máy này.');
  };
  $('mineList').addEventListener('click', async (e) => {
    const del = e.target.closest('[data-del]');
    if (del) {
      e.stopPropagation(); const m = S.mine[+del.dataset.del]; if (!m) return;
      const v = await ask('Xoá mẫu "' + m.name + '" khỏi máy? Gõ OK để xác nhận.', 'OK'); if (String(v || '').trim().toUpperCase() !== 'OK') return;
      try { await call('BeginDeleteMine', m.name); } catch (er) { toast(er.message || String(er), true); }
      refreshMine(); return;
    }
    const it = e.target.closest('.it'); if (it && S.mine[+it.dataset.i]) openMine(S.mine[+it.dataset.i].name);
  });
  $('bSave').onclick = () => saveMine(true);
  document.addEventListener('keydown', (e) => {
    if (!(e.ctrlKey || e.metaKey) || e.altKey || e.key.toLowerCase() !== 's') return;
    e.preventDefault(); e.stopPropagation();
    saveMine(!e.shiftKey);
  }, true);
  $('bSaveFile').onclick = async () => {
    const t = getText(); if (!t) { toast('Chưa có thiết kế.', true); return; }
    const p = await call('BeginSaveText', fileBase() + '_design.f', 'Controller FastBusiness (*.f)|*.f|XML (*.xml)|*.xml', t);
    if (p) toast('Đã lưu thiết kế: ' + p);
  };
  refreshMine();

  // ------------------------------------------------------------------ khởi động
  const ctx = JSON.parse(await call('BeginGetContext'));
  S.workspaces = ctx.workspaces || []; S.viewer = ctx.viewer || ''; S.state = ctx.state || {}; S.tests = S.state.tests || {};
  if (S.state.mineDir) { try { await call('BeginSetMineDir', S.state.mineDir); } catch { S.state.mineDir = ''; } }
  S.source = ctx.source || S.state.source || (S.workspaces.find((w) => w.name === ctx.project) || {}).source || '';
  if (!S.source) S.source = 'D:\\Bee\\FBO\\Program\\FastBusinessOnline';
  if (S.state.zoom) $('zoom').value = S.state.zoom;
  S.custom = (S.state.custom || []).map((c) => Object.assign(parseMine(c.name, c.src || ''), { id: c.id })); renderComps();
  if (S.state.vars === false) $('optVar').checked = false;
  $('dpRoot').classList.toggle('noVar', !$('optVar').checked);
  renderProjects();
  await refreshMirror();
  S.useMirror = S.state.useMirror !== false;
  $('useMirror').checked = S.useMirror;
  await applyCache();
  await loadScreens(false);
  // lần đầu chưa có bản nạp sẵn: tự chép ngầm về máy (lần sau mở là nhanh); xong sẽ tự chuyển sang dùng
  if (S.mirror && !S.mirror.exists && !S.mirror.running) { try { await call('BeginMirrorStart', S.source, ''); S.mirrorWasRunning = true; refreshMirror(); } catch { /* ổ lỗi: bỏ qua */ } }
  applyZoom();
  await refreshMine();
  const want = ctx.controller ? { k: ctx.kind || 'Dir', n: ctx.controller } : (S.state.last || null);
  if (want) {
    const hit = S.screens.find((s) => s.n.toLowerCase() === want.n.toLowerCase() && s.k.toLowerCase() === (want.k || 'Dir').toLowerCase()) || S.screens.find((s) => s.n.toLowerCase() === want.n.toLowerCase());
    if (hit) await openScreen(hit);
  }
  syncAll();
})();
