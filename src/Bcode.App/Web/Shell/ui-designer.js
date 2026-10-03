/* Công cụ thiết kế giao diện trực quan (Template giao diện → "HTML & CSS" → Chế độ thiết kế).
   UiTemplate.BuildScript nạp file này vào MỌI trang web khi chế độ thiết kế bật: nút ✎ ở góc dưới phải mở bảng thiết kế với 3 tab:
     • Phần tử  — chọn phần tử trên trang rồi đổi style (ẩn / màu / cỡ chữ / bo góc / đệm ...), chữ hiển thị, placeholder, tooltip, đẩy lên/xuống;
     • Cây HTML — cây DOM của trang: bấm để chọn, kéo-thả để dời phần tử (trước / sau / vào trong), con mắt để ẩn / hiện;
     • Thay đổi — danh sách thao tác cấu trúc đã làm (đổi chữ, thuộc tính, dời chỗ) để hoàn tác / xoá.
   Style (CSS) lưu thành khối "bcode-designer" trong <trang>.css; thao tác cấu trúc lưu thành bản vá <trang>.patch.json (mảng {op, sel, ...}) và được áp lại
   sau mỗi lần nạp trang (UiOverrides.PatchApplyScript). Xử lý (backend) tìm phần tử theo id nên không bị ảnh hưởng. */
(function () {
  if (window.__bcodeDesigner) { window.__bcodeDesignerSync && window.__bcodeDesignerSync(true); return; }
  window.__bcodeDesigner = true;

  var PAGE = location.pathname.split('/').pop();
  var BEGIN = '/* bcode-designer:start */', END = '/* bcode-designer:end */';
  var ROOT = document.documentElement;                 // công cụ gắn vào <html> (không phải <body>) để không làm lệch chỉ số nth-of-type của trang
  var rules = {}, savedRules = '';                     // CSS: selector -> { thuộc-tính: giá-trị }
  var ops = [], savedOps = '', baseCount = 0, undo = []; // bản vá cấu trúc; undo[i] hoàn tác trực tiếp thao tác mới thứ i (ops[baseCount + i])
  var needReload = false;                              // đã xoá thao tác đã lưu từ trước → chỉ có tác dụng sau khi lưu & nạp lại
  var picking = false, current = null, currentEl = null, scope = 'one', tab = 'el';
  var btn, panel, hl, live, dragEl = null, expanded = new WeakSet();

  var PROPS = [
    ['color', 'Màu chữ', 'color'], ['background', 'Màu nền', 'color'], ['font-size', 'Cỡ chữ (px)', 'px'], ['font-weight', 'Đậm', 'bold'],
    ['border-radius', 'Bo góc (px)', 'px'], ['padding', 'Đệm trong (px)', 'px'], ['margin', 'Lề ngoài (px)', 'px'], ['width', 'Rộng (vd 200px, 50%)', 'text']
  ];
  var SKIP = { SCRIPT: 1, STYLE: 1, LINK: 1, META: 1, BASE: 1, NOSCRIPT: 1, TEMPLATE: 1, HEAD: 1, TITLE: 1 };
  var VOID = { INPUT: 1, IMG: 1, BR: 1, HR: 1, SELECT: 1, TEXTAREA: 1, BUTTON: 1, OPTION: 1 };

  // ---------------------------------------------------------------- tiện ích
  function isDesignerNode(n) { return n && n.id && /^bcode-/.test(n.id); }
  function inUi(el) { return !!(el && el.closest && el.closest('#bcode-designer-ui,#bcode-designer-btn,#bcode-designer-hl')); }
  function designable(el) { return el && el.nodeType === 1 && !SKIP[el.tagName] && !isDesignerNode(el) && !inUi(el); }
  function kids(el) { return Array.prototype.filter.call(el.children, designable); }
  function esc(s) { return window.CSS && CSS.escape ? CSS.escape(s) : s.replace(/[^\w-]/g, '\\$&'); }
  function el(tag, props, children) {
    var n = document.createElement(tag);
    Object.keys(props || {}).forEach(function (k) { if (k === 'text') n.textContent = props[k]; else if (k === 'cls') n.className = props[k]; else n[k] = props[k]; });
    (children || []).forEach(function (c) { n.appendChild(c); });
    return n;
  }
  function toHex(c) {
    var m = /rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(c || ''); if (!m) return '#888888';
    return '#' + [m[1], m[2], m[3]].map(function (n) { return ('0' + (+n).toString(16)).slice(-2); }).join('');
  }

  // ---------------------------------------------------------------- CSS (khối thiết kế)
  function pageRaw() { return (window.__bcodeUiRaw || {})[PAGE] || ''; }
  function parseBlock(css) {
    var out = {}, i = css.indexOf(BEGIN), j = css.indexOf(END);
    if (i < 0 || j < i) return out;
    css.substring(i + BEGIN.length, j).split('\n').forEach(function (line) {
      var m = /^\s*(.+?)\s*\{\s*(.*?)\s*\}\s*$/.exec(line); if (!m) return;
      var props = {};
      m[2].split(';').forEach(function (d) { var k = d.indexOf(':'); if (k < 0) return; var name = d.substring(0, k).trim(), v = d.substring(k + 1).replace(/!important/i, '').trim(); if (name) props[name] = v; });
      out[m[1]] = props;
    });
    return out;
  }
  function cssBlock() {
    var lines = Object.keys(rules).filter(function (s) { return Object.keys(rules[s]).length; }).map(function (s) {
      return s + ' { ' + Object.keys(rules[s]).map(function (k) { return k + ': ' + rules[s][k] + ' !important'; }).join('; ') + '; }';
    });
    return BEGIN + '\n' + lines.join('\n') + (lines.length ? '\n' : '') + END;
  }
  // Đang thiết kế: bản nháp (live) thay cho khối đã lưu trong style người dùng, để sửa/xoá quy tắc thấy ngay.
  function applyLive() {
    var m = window.__bcodeUiRaw || {}, page = m[PAGE] || '', i = page.indexOf(BEGIN), j = page.indexOf(END);
    var without = (i >= 0 && j > i) ? page.substring(0, i) + page.substring(j + END.length) : page;
    var st = document.getElementById('bcode-user-css');
    if (!st) { st = document.createElement('style'); st.id = 'bcode-user-css'; document.head.appendChild(st); }
    st.textContent = (m['*'] || '') + '\n' + without;
    if (!live) { live = document.createElement('style'); live.id = 'bcode-designer-live'; document.head.appendChild(live); }
    live.textContent = cssBlock();
  }
  function restoreCss() {
    if (live) live.textContent = '';
    var m = window.__bcodeUiRaw || {}, st = document.getElementById('bcode-user-css');
    if (st) st.textContent = (m['*'] || '') + '\n' + (m[PAGE] || '');
  }

  // ---------------------------------------------------------------- bộ chọn
  function cleanClasses(e) {
    return Array.prototype.slice.call(e.classList).filter(function (c) { return !/^(sel|selected|active|on|over|hover|focus|open|dragging|rec|bcode|dz-)/i.test(c); }).slice(0, 2);
  }
  function sameTagSiblings(e) {
    return e.parentNode ? Array.prototype.filter.call(e.parentNode.children, function (n) { return n.tagName === e.tagName && !isDesignerNode(n); }) : [];
  }
  function selectorFor(e, mode) {
    if (mode === 'all') {
      var c = cleanClasses(e);
      return e.tagName.toLowerCase() + c.map(function (x) { return '.' + esc(x); }).join('');
    }
    if (e.id && !isDesignerNode(e) && document.querySelectorAll('#' + esc(e.id)).length === 1) return '#' + esc(e.id);
    var parts = [], cur = e;
    while (cur && cur !== document.body && cur.nodeType === 1 && parts.length < 8) {
      if (cur.id && !isDesignerNode(cur) && document.querySelectorAll('#' + esc(cur.id)).length === 1) { parts.unshift('#' + esc(cur.id)); break; }
      var seg = cur.tagName.toLowerCase() + cleanClasses(cur).map(function (x) { return '.' + esc(x); }).join('');
      var sibs = sameTagSiblings(cur);
      if (sibs.length > 1) seg += ':nth-of-type(' + (sibs.indexOf(cur) + 1) + ')';
      parts.unshift(seg); cur = cur.parentElement;
    }
    if (parts[0] && parts[0].charAt(0) !== '#') parts.unshift('body');
    return parts.join(' > ');
  }
  function oneSel(e) { return selectorFor(e, 'one'); }
  function short(sel) { return sel.length > 34 ? '…' + sel.slice(-33) : sel; }
  function matches(sel) { try { return document.querySelectorAll(sel).length; } catch (e) { return 0; } }

  // ---------------------------------------------------------------- thao tác cấu trúc (áp trực tiếp + ghi vào ops)
  function ownTextNode(e) { for (var i = 0; i < e.childNodes.length; i++) { var n = e.childNodes[i]; if (n.nodeType === 3 && n.nodeValue.trim()) return n; } return null; }
  function ownText(e) { var n = ownTextNode(e); return n ? n.nodeValue.trim() : (!e.children.length ? (e.textContent || '').trim() : ''); }
  function setText(e, v) {
    var n = ownTextNode(e);
    if (n) { var old = n.nodeValue; n.nodeValue = v; return function () { n.nodeValue = old; }; }
    if (!e.children.length) { var o = e.textContent; e.textContent = v; return function () { e.textContent = o; }; }
    var t = document.createTextNode(v); e.insertBefore(t, e.firstChild); return function () { t.remove(); };
  }
  function setAttr(e, name, v) {
    var had = e.hasAttribute(name), old = e.getAttribute(name);
    if (v === '') e.removeAttribute(name); else e.setAttribute(name, v);
    return function () { if (had) e.setAttribute(name, old); else e.removeAttribute(name); };
  }
  function doMove(e, r, pos) {
    if (!r || r === e || e.contains(r)) return null;
    var parent = e.parentNode, next = e.nextSibling;
    if (pos === 'before') r.parentNode.insertBefore(e, r);
    else if (pos === 'after') r.parentNode.insertBefore(e, r.nextSibling);
    else r.appendChild(e);
    return function () { parent.insertBefore(e, next); };
  }
  function addOp(o, undoFn) { ops.push(o); undo.push(undoFn || null); }
  // text / attr: mỗi (phần tử, thuộc tính) chỉ giữ 1 thao tác — sửa lại thì cập nhật giá trị của thao tác cũ.
  function upsert(kind, sel, name, value, target) {
    var idx = -1;
    ops.forEach(function (o, i) { if (o.op === kind && o.sel === sel && (kind === 'text' || o.name === name)) idx = i; });
    if (idx >= 0) {
      ops[idx].value = value;
      if (kind === 'text') setText(target, value); else setAttr(target, name, value);
      return;
    }
    var u = kind === 'text' ? setText(target, value) : setAttr(target, name, value);
    var o = { op: kind, sel: sel, value: value }; if (kind === 'attr') o.name = name;
    addOp(o, u);
  }
  function recordMove(e, r, pos) {
    var s = oneSel(e), rs = oneSel(r);
    var u = doMove(e, r, pos); if (!u) return false;
    addOp({ op: 'move', sel: s, ref: rs, pos: pos }, u);
    return true;
  }
  function undoLast() {
    if (!ops.length) return;
    var i = ops.length - 1;
    if (i >= baseCount && undo[i - baseCount]) { undo[i - baseCount](); undo.pop(); ops.pop(); }
    else { ops.pop(); needReload = true; baseCount = Math.min(baseCount, ops.length); undo.length = Math.max(0, ops.length - baseCount); }
  }
  function removeOp(i) {
    if (i === ops.length - 1) { undoLast(); return; }
    ops.splice(i, 1); needReload = true; // xoá giữa chừng: DOM hiện tại chưa quay về được — sau khi lưu & nạp lại mới đúng
    undo = []; baseCount = 0;
  }
  function describe(o) {
    if (o.op === 'text') return 'Chữ: ' + short(o.sel) + ' → "' + (o.value.length > 30 ? o.value.slice(0, 29) + '…' : o.value) + '"';
    if (o.op === 'attr') return o.name + ': ' + short(o.sel) + ' → "' + (o.value.length > 24 ? o.value.slice(0, 23) + '…' : o.value) + '"';
    return 'Dời: ' + short(o.sel) + ' → ' + ({ before: 'trước ', after: 'sau ', append: 'vào trong ' }[o.pos] || '') + short(o.ref);
  }

  // ---------------------------------------------------------------- giao diện công cụ
  var UI_CSS = '#bcode-designer-btn{position:fixed;right:8px;bottom:8px;z-index:2147483646;width:26px;height:26px;border-radius:13px;border:1px solid #f5a623;background:#2d2d30;color:#f5a623;font:14px/24px sans-serif;text-align:center;cursor:pointer;opacity:.7;padding:0}' +
    '#bcode-designer-btn:hover{opacity:1}' +
    '#bcode-designer-ui{position:fixed;right:8px;bottom:42px;z-index:2147483646;width:340px;max-height:calc(100vh - 56px);display:flex;flex-direction:column;background:#2d2d30;color:#ddd;border:1px solid #f5a623;border-radius:8px;font:12px/1.4 "Segoe UI",sans-serif;box-shadow:0 8px 28px rgba(0,0,0,.55);user-select:none}' +
    '#bcode-designer-ui *{box-sizing:border-box;font:inherit;color:inherit}' +
    '#bcode-designer-ui .h{display:flex;gap:6px;align-items:center;padding:6px 10px;background:#3a3a3e;border-bottom:1px solid #555;font-weight:600;border-radius:8px 8px 0 0;flex:none}' +
    '#bcode-designer-ui .h .t{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
    '#bcode-designer-ui .tabs{display:flex;border-bottom:1px solid #555;flex:none}' +
    '#bcode-designer-ui .tabs button{flex:1;border:none;border-radius:0;background:transparent;padding:5px 4px;border-bottom:2px solid transparent;cursor:pointer}' +
    '#bcode-designer-ui .tabs button.on{border-bottom-color:#f5a623;color:#f5a623;font-weight:600}' +
    '#bcode-designer-ui .b{padding:8px 10px;display:flex;flex-direction:column;gap:6px;overflow:auto;min-height:0}' +
    '#bcode-designer-ui .f{padding:6px 10px;border-top:1px solid #555;display:flex;flex-direction:column;gap:4px;flex:none}' +
    '#bcode-designer-ui button{background:#3e3e42;border:1px solid #666;border-radius:5px;padding:3px 9px;cursor:pointer}' +
    '#bcode-designer-ui button:hover{border-color:#f5a623}' +
    '#bcode-designer-ui button:disabled{opacity:.4;cursor:default}' +
    '#bcode-designer-ui button.p{background:#f5a623;color:#1c1608;border-color:#f5a623;font-weight:600}' +
    '#bcode-designer-ui button.on{background:#5c3e09;border-color:#f5a623}' +
    '#bcode-designer-ui .r{display:grid;grid-template-columns:104px 1fr auto;gap:6px;align-items:center}' +
    '#bcode-designer-ui input[type=text],#bcode-designer-ui select{width:100%;background:#3c3c3c;border:1px solid #666;border-radius:4px;padding:2px 6px;color:#eee;user-select:text}' +
    '#bcode-designer-ui input[type=color]{width:100%;height:24px;padding:0;border:1px solid #666;background:#3c3c3c}' +
    '#bcode-designer-ui .m{color:#aaa}' +
    '#bcode-designer-ui .warn{color:#ffb454}' +
    '#bcode-designer-ui .sel{font-family:Consolas,monospace;font-size:11px;word-break:break-all;color:#f5a623;user-select:text}' +
    '#bcode-designer-ui .row2{display:flex;gap:6px;flex-wrap:wrap}' +
    '#bcode-designer-ui .tree{font-family:Consolas,monospace;font-size:11.5px}' +
    '#bcode-designer-ui .n{display:flex;align-items:center;gap:3px;padding:1px 3px;border-radius:3px;cursor:pointer;white-space:nowrap;border-top:2px solid transparent;border-bottom:2px solid transparent}' +
    '#bcode-designer-ui .n:hover{background:#3c3c3c}' +
    '#bcode-designer-ui .n.cur{background:#5c3e09}' +
    '#bcode-designer-ui .n.dz-before{border-top-color:#f5a623}#bcode-designer-ui .n.dz-after{border-bottom-color:#f5a623}#bcode-designer-ui .n.dz-in{background:#2d4a7a}' +
    '#bcode-designer-ui .n .c{width:12px;text-align:center;color:#999;flex:none}' +
    '#bcode-designer-ui .n .l{overflow:hidden;text-overflow:ellipsis;flex:1}' +
    '#bcode-designer-ui .n .l i{color:#8ab4f8;font-style:normal}#bcode-designer-ui .n .l u{color:#999;text-decoration:none}' +
    '#bcode-designer-ui .n .eye{opacity:.5;padding:0 3px;border:none;background:transparent}#bcode-designer-ui .n .eye:hover{opacity:1}' +
    '#bcode-designer-ui .op{display:flex;gap:6px;align-items:center;padding:2px 0;border-bottom:1px solid #444}' +
    '#bcode-designer-ui .op span{flex:1;word-break:break-all;user-select:text}' +
    '#bcode-designer-hl{position:fixed;z-index:2147483645;pointer-events:none;border:2px solid #f5a623;background:rgba(245,166,35,.18);border-radius:3px;display:none}';

  function build() {
    document.head.appendChild(el('style', { id: 'bcode-designer-style', textContent: UI_CSS }));
    btn = el('button', { id: 'bcode-designer-btn', title: 'Thiết kế giao diện trang này', text: '✎' });
    btn.onclick = function () { panel ? closePanel() : openPanel(); };
    hl = el('div', { id: 'bcode-designer-hl' });
    ROOT.appendChild(btn); ROOT.appendChild(hl);
    document.addEventListener('mouseover', onOver, true);
    document.addEventListener('click', onClick, true);
    document.addEventListener('keydown', onKey, true);
  }
  function onKey(e) { if (e.key === 'Escape' && picking) { setPicking(false); renderPanel(); } }

  function dirty() { return JSON.stringify(rules) !== savedRules || JSON.stringify(ops) !== savedOps || needReload; }
  function closePanel() {
    if (dirty() && !confirm('Có thay đổi thiết kế chưa lưu — bỏ qua? (thay đổi cấu trúc đã làm sẽ mất khi nạp lại trang)')) return;
    if (panel) { panel.remove(); panel = null; }
    setPicking(false); hl.style.display = 'none';
    restoreCss();
  }
  function openPanel() {
    rules = parseBlock(pageRaw()); savedRules = JSON.stringify(rules);
    var saved = (window.__bcodePatches || {})[PAGE] || [];
    ops = JSON.parse(JSON.stringify(saved)); savedOps = JSON.stringify(ops); baseCount = ops.length; undo = []; needReload = false;
    applyLive();
    panel = el('div', { id: 'bcode-designer-ui' });
    ROOT.appendChild(panel);
    renderPanel();
  }

  function renderPanel() {
    if (!panel) return;
    var oldBody = panel.querySelector('.b'), keepScroll = oldBody ? oldBody.scrollTop : 0;
    panel.textContent = '';
    var pickBtn = el('button', { text: picking ? 'Đang chọn... (Esc)' : '⌖ Chọn', cls: picking ? 'on' : '', title: 'Bấm vào một phần tử trên trang để chọn' });
    pickBtn.onclick = function () { setPicking(!picking); renderPanel(); };
    var close = el('button', { text: '✕', title: 'Đóng công cụ' }); close.onclick = closePanel;
    panel.appendChild(el('div', { cls: 'h' }, [el('span', { cls: 't', text: 'Thiết kế · ' + PAGE }), pickBtn, close]));

    var tabs = el('div', { cls: 'tabs' });
    [['el', 'Phần tử'], ['tree', 'Cây HTML'], ['ops', 'Thay đổi (' + ops.length + ')']].forEach(function (t) {
      var b = el('button', { text: t[1], cls: tab === t[0] ? 'on' : '' }); b.onclick = function () { tab = t[0]; renderPanel(); }; tabs.appendChild(b);
    });
    panel.appendChild(tabs);

    var body = el('div', { cls: 'b' });
    if (tab === 'el') renderElement(body); else if (tab === 'tree') renderTree(body); else renderOps(body);
    panel.appendChild(body);

    var foot = el('div', { cls: 'f' });
    var save = el('button', { text: 'Lưu tất cả', cls: 'p', title: 'Lưu style vào CSS của trang và thao tác cấu trúc vào bản vá' });
    save.onclick = saveAll;
    foot.appendChild(el('div', { cls: 'row2' }, [save]));
    foot.appendChild(el('div', { cls: dirty() ? 'warn' : 'm', text: statusText() }));
    panel.appendChild(foot);
    body.scrollTop = keepScroll;
  }
  function statusText() {
    if (!dirty()) return 'Đã lưu · CSS tay / JSON bản vá: Giao diện → "HTML & CSS"';
    return '● Chưa lưu' + (JSON.stringify(ops) !== savedOps || needReload ? ' — lưu thay đổi cấu trúc sẽ nạp lại trang' : '');
  }
  // chỉ đổi dòng trạng thái + số thao tác trên tab, không dựng lại cả bảng (tránh mất focus ô đang gõ)
  function softRefresh() {
    if (!panel) return;
    var f = panel.querySelector('.f > div:last-child'); if (f) { f.className = dirty() ? 'warn' : 'm'; f.textContent = statusText(); }
    var t = panel.querySelectorAll('.tabs button')[2]; if (t) t.textContent = 'Thay đổi (' + ops.length + ')';
  }

  function saveAll() {
    Object.keys(rules).forEach(function (s) { if (!Object.keys(rules[s]).length) delete rules[s]; });
    var cssDirty = JSON.stringify(rules) !== savedRules, opsDirty = JSON.stringify(ops) !== savedOps || needReload;
    if (cssDirty) { window.chrome.webview.postMessage(JSON.stringify({ action: '__ui-designer-save', page: PAGE, block: cssBlock() })); savedRules = JSON.stringify(rules); }
    if (opsDirty) { window.chrome.webview.postMessage(JSON.stringify({ action: '__ui-patches-save', page: PAGE, ops: ops })); savedOps = JSON.stringify(ops); needReload = false; }
    renderPanel();
  }

  // ---- tab Phần tử ----
  function renderElement(b) {
    if (!current || !currentEl || !currentEl.isConnected) {
      b.appendChild(el('div', { cls: 'm', text: 'Bấm "⌖ Chọn" rồi bấm vào nút / ô / nhãn trên trang (hoặc chọn trong tab "Cây HTML") để đổi giao diện của nó. Xử lý của chương trình không bị ảnh hưởng.' }));
      return;
    }
    var sel1 = oneSel(currentEl);
    b.appendChild(el('div', { cls: 'sel', text: sel1 }));
    var sc = el('select'); [['one', 'Style: chỉ phần tử này'], ['all', 'Style: mọi phần tử cùng loại']].forEach(function (o) { sc.appendChild(new Option(o[1], o[0])); }); sc.value = scope;
    sc.onchange = function () { scope = sc.value; renderPanel(); };
    b.appendChild(sc);
    current = selectorFor(currentEl, scope);
    b.appendChild(el('div', { cls: 'm', text: 'Style áp cho ' + matches(current) + ' phần tử (' + current + ')' }));

    // chữ hiển thị / placeholder / tooltip (bản vá cấu trúc)
    var text = ownText(currentEl);
    if ((!VOID[currentEl.tagName] || currentEl.tagName === 'BUTTON') && (text || !currentEl.children.length)) {
      var ti = el('input', { type: 'text', value: text, placeholder: '(không có chữ riêng)' });
      ti.oninput = function () { upsert('text', sel1, '', ti.value, currentEl); softRefresh(); };
      b.appendChild(el('div', { cls: 'r' }, [el('span', { text: 'Chữ hiển thị' }), ti, el('span')]));
    }
    if (currentEl.tagName === 'INPUT' || currentEl.tagName === 'TEXTAREA') {
      var pi = el('input', { type: 'text', value: currentEl.getAttribute('placeholder') || '' });
      pi.oninput = function () { upsert('attr', sel1, 'placeholder', pi.value, currentEl); softRefresh(); };
      b.appendChild(el('div', { cls: 'r' }, [el('span', { text: 'Placeholder' }), pi, el('span')]));
    }
    var tt = el('input', { type: 'text', value: currentEl.getAttribute('title') || '' });
    tt.oninput = function () { upsert('attr', sel1, 'title', tt.value, currentEl); softRefresh(); };
    b.appendChild(el('div', { cls: 'r' }, [el('span', { text: 'Tooltip (title)' }), tt, el('span')]));

    // dời lên / xuống trong cùng cha
    var sibs = currentEl.parentElement ? kids(currentEl.parentElement) : [], at = sibs.indexOf(currentEl);
    var up = el('button', { text: '▲ Lên', title: 'Dời lên trước phần tử anh em phía trên' }), down = el('button', { text: '▼ Xuống', title: 'Dời xuống sau phần tử anh em phía dưới' });
    up.disabled = at <= 0; down.disabled = at < 0 || at >= sibs.length - 1;
    up.onclick = function () { if (recordMove(currentEl, sibs[at - 1], 'before')) { renderPanel(); highlight(currentEl); } };
    down.onclick = function () { if (recordMove(currentEl, sibs[at + 1], 'after')) { renderPanel(); highlight(currentEl); } };
    b.appendChild(el('div', { cls: 'r' }, [el('span', { text: 'Vị trí' }), el('div', { cls: 'row2' }, [up, down]), el('span')]));

    // style (CSS)
    var r = rules[current] || (rules[current] = {});
    var hid = el('input', { type: 'checkbox' }); hid.checked = r['display'] === 'none';
    hid.onchange = function () { if (hid.checked) r['display'] = 'none'; else delete r['display']; cssChanged(); };
    b.appendChild(el('div', { cls: 'r' }, [el('span', { text: 'Ẩn phần tử' }), hid, el('span')]));
    var cs = getComputedStyle(currentEl);
    PROPS.forEach(function (p) {
      var name = p[0], kind = p[2], inp, val = r[name] || '';
      if (kind === 'color') { inp = el('input', { type: 'color' }); inp.value = /^#[0-9a-f]{6}$/i.test(val) ? val : toHex(cs[name === 'background' ? 'backgroundColor' : name]); inp.oninput = function () { r[name] = inp.value; cssChanged(); }; }
      else if (kind === 'bold') { inp = el('select'); [['', '(giữ nguyên)'], ['700', 'In đậm'], ['400', 'Thường']].forEach(function (o) { inp.appendChild(new Option(o[1], o[0])); }); inp.value = val; inp.onchange = function () { if (inp.value) r[name] = inp.value; else delete r[name]; cssChanged(); }; }
      else if (kind === 'px') { inp = el('input', { type: 'text', placeholder: parseFloat(cs[name]) ? Math.round(parseFloat(cs[name])) + '' : '' }); inp.value = val.replace('px', ''); inp.oninput = function () { var v = inp.value.trim(); if (v && !isNaN(+v)) r[name] = (+v) + 'px'; else if (!v) delete r[name]; cssChanged(); }; }
      else { inp = el('input', { type: 'text' }); inp.value = val; inp.oninput = function () { var v = inp.value.trim(); if (v) r[name] = v; else delete r[name]; cssChanged(); }; }
      var clear = el('button', { text: '↺', title: 'Bỏ quy tắc này' }); clear.onclick = function () { delete r[name]; cssChanged(); renderPanel(); };
      b.appendChild(el('div', { cls: 'r' }, [el('span', { text: p[1] }), inp, clear]));
    });
    var rm = el('button', { text: 'Xoá quy tắc style của phần tử này' }); rm.onclick = function () { delete rules[current]; cssChanged(); renderPanel(); };
    b.appendChild(rm);
  }
  function cssChanged() { applyLive(); softRefresh(); }

  // ---- tab Cây HTML ----
  function label(e) {
    var s = '<i>' + e.tagName.toLowerCase() + '</i>';
    if (e.id) s += '#' + e.id.replace(/</g, '&lt;');
    var c = cleanClasses(e); if (c.length) s += '.' + c.join('.').replace(/</g, '&lt;');
    var t = ownText(e); if (t) s += ' <u>' + (t.length > 22 ? t.slice(0, 21) + '…' : t).replace(/</g, '&lt;') + '</u>';
    return s;
  }
  function canHold(e) { return !VOID[e.tagName]; }
  function zone(ev, row, e) {
    var r = row.getBoundingClientRect(), y = (ev.clientY - r.top) / r.height;
    if (e === document.body) return 'append';
    if (canHold(e) && y > 0.3 && y < 0.7) return 'append';
    return y < 0.5 ? 'before' : 'after';
  }
  function clearDz() { if (panel) panel.querySelectorAll('.dz-before,.dz-after,.dz-in').forEach(function (n) { n.classList.remove('dz-before', 'dz-after', 'dz-in'); }); }
  function renderTree(b) {
    b.appendChild(el('div', { cls: 'm', text: 'Bấm để chọn · kéo-thả để dời (thả giữa dòng = vào trong) · 👁 ẩn/hiện · bấm đúp để sửa.' }));
    var host = el('div', { cls: 'tree' }), count = 0, LIMIT = 1500;
    if (currentEl && currentEl.isConnected) for (var p = currentEl.parentElement; p; p = p.parentElement) expanded.add(p);   // mở sẵn đường tới phần tử đang chọn
    expanded.add(document.body);
    function node(e, depth) {
      if (count++ > LIMIT) return;
      var ks = kids(e), open = expanded.has(e), hasKids = ks.length > 0;
      var row = el('div', { cls: 'n' + (e === currentEl ? ' cur' : ''), draggable: e !== document.body });
      row.style.paddingLeft = (depth * 12 + 3) + 'px';
      var caret = el('span', { cls: 'c', text: hasKids ? (open ? '▾' : '▸') : '' });
      caret.onclick = function (ev) { ev.stopPropagation(); if (open) expanded.delete(e); else expanded.add(e); renderPanel(); };
      var lab = el('span', { cls: 'l' }); lab.innerHTML = label(e);
      var rs = rules[selectorFor(e, 'one')], hidden = rs && rs['display'] === 'none';
      var eye = el('button', { cls: 'eye', text: hidden ? '🚫' : '👁', title: 'Ẩn / hiện phần tử này' });
      eye.onclick = function (ev) {
        ev.stopPropagation();
        var s = selectorFor(e, 'one'), r = rules[s] || (rules[s] = {});
        if (r['display'] === 'none') delete r['display']; else r['display'] = 'none';
        applyLive(); renderPanel();
      };
      row.appendChild(caret); row.appendChild(lab); if (e !== document.body) row.appendChild(eye);
      row.onclick = function () { select(e); renderPanel(); };
      row.ondblclick = function () { select(e); tab = 'el'; renderPanel(); };
      row.onmouseenter = function () { highlight(e); };
      row.onmouseleave = function () { if (!picking) hl.style.display = 'none'; };
      row.ondragstart = function (ev) { dragEl = e; ev.dataTransfer.effectAllowed = 'move'; try { ev.dataTransfer.setData('text/plain', 'x'); } catch (x) { } };
      row.ondragend = function () { dragEl = null; clearDz(); };
      row.ondragover = function (ev) {
        if (!dragEl || dragEl === e || dragEl.contains(e)) return;
        ev.preventDefault(); clearDz();
        var z = zone(ev, row, e); row.classList.add(z === 'before' ? 'dz-before' : z === 'after' ? 'dz-after' : 'dz-in');
      };
      row.ondragleave = function () { row.classList.remove('dz-before', 'dz-after', 'dz-in'); };
      row.ondrop = function (ev) {
        ev.preventDefault();
        if (!dragEl || dragEl === e || dragEl.contains(e)) return;
        var z = zone(ev, row, e), moved = dragEl; dragEl = null; clearDz();
        if (recordMove(moved, e, z)) { select(moved); if (moved.parentElement) expanded.add(moved.parentElement); renderPanel(); }
      };
      host.appendChild(row);
      if (hasKids && open) ks.forEach(function (k) { node(k, depth + 1); });
    }
    node(document.body, 0);
    if (count > LIMIT) host.appendChild(el('div', { cls: 'warn', text: 'Trang có rất nhiều phần tử — chỉ hiện ' + LIMIT + ' dòng đầu.' }));
    b.appendChild(host);
  }

  // ---- tab Thay đổi ----
  function renderOps(b) {
    if (!ops.length) { b.appendChild(el('div', { cls: 'm', text: 'Chưa có thay đổi cấu trúc nào (đổi chữ, thuộc tính, dời chỗ). Làm ở tab "Phần tử" hoặc "Cây HTML".' })); return; }
    ops.forEach(function (o, i) {
      var x = el('button', { text: '✕', title: i === ops.length - 1 ? 'Hoàn tác thao tác này' : 'Xoá thao tác (cần lưu & nạp lại để thấy)' });
      x.onclick = function () { removeOp(i); renderPanel(); };
      var bad = o.op !== 'move' && i >= baseCount && !matches(o.sel);
      b.appendChild(el('div', { cls: 'op' }, [el('span', { text: (i + 1) + '. ' + describe(o) + (bad ? '  ⚠ không khớp' : '') }), x]));
    });
    var undoBtn = el('button', { text: '↶ Hoàn tác thao tác cuối' }); undoBtn.onclick = function () { undoLast(); renderPanel(); };
    var all = el('button', { text: 'Xoá hết thay đổi cấu trúc' });
    all.onclick = function () { if (confirm('Xoá toàn bộ thay đổi cấu trúc của trang này? (lưu và nạp lại trang để thấy)')) { ops = []; undo = []; baseCount = 0; needReload = true; renderPanel(); } };
    b.appendChild(el('div', { cls: 'row2' }, [undoBtn, all]));
    if (needReload) b.appendChild(el('div', { cls: 'warn', text: 'Đã xoá thao tác giữa chừng — bấm "Lưu tất cả" để trang nạp lại và áp đúng danh sách còn lại. Thao tác sau có thể không còn khớp phần tử nếu phụ thuộc thao tác đã xoá.' }));
  }

  // ---------------------------------------------------------------- chọn phần tử
  function highlight(e) {
    if (!e || !e.isConnected) { hl.style.display = 'none'; return; }
    var r = e.getBoundingClientRect();
    hl.style.cssText = 'display:block;left:' + r.left + 'px;top:' + r.top + 'px;width:' + r.width + 'px;height:' + r.height + 'px';
  }
  function select(e) { currentEl = e; current = selectorFor(e, scope); highlight(e); try { e.scrollIntoView({ block: 'nearest', inline: 'nearest' }); } catch (x) { } }
  function setPicking(on) { picking = on; document.body.style.cursor = on ? 'crosshair' : ''; if (!on && hl) hl.style.display = 'none'; }
  function onOver(e) { if (picking && !inUi(e.target)) highlight(e.target); }
  function onClick(e) {
    if (!picking || inUi(e.target)) return;
    e.preventDefault(); e.stopPropagation();
    setPicking(false); select(e.target); tab = 'el'; renderPanel();
  }

  // Gọi từ UiTemplate.BuildScript mỗi lần cấu hình đổi: tắt chế độ thiết kế thì gỡ nút/khung.
  window.__bcodeDesignerSync = function (on) {
    if (on) { if (!btn || !btn.isConnected) build(); return; }
    if (panel) { panel.remove(); panel = null; }
    setPicking(false);
    [btn, hl, document.getElementById('bcode-designer-style'), live].forEach(function (n) { if (n) n.remove(); });
    btn = hl = live = null;
    document.removeEventListener('mouseover', onOver, true); document.removeEventListener('click', onClick, true); document.removeEventListener('keydown', onKey, true);
    window.__bcodeDesigner = false;
  };
  // Móc để kiểm thử tự động (không dùng trong vận hành).
  window.__bcodeDesignerApi = { ops: function () { return ops; }, open: openPanel, select: select, upsert: upsert, move: recordMove, undo: undoLast, selectorFor: selectorFor };

  if (document.body) build(); else document.addEventListener('DOMContentLoaded', build);
})();
