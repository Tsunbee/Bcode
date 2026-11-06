// Ô phần trăm phóng to chữ — hiện ở đầu bên phải toolbar, ngang hàng với các nút: "[150%]  −  +  Đặt lại". Toolbar ẩn thì ô ẩn theo.
// Ctrl + lăn chuột (zoom có sẵn của Monaco) đổi cỡ chữ; ô này cho biết đang ở mức nào, gõ số % cụ thể + Enter để đặt, hoặc dùng − / + / Đặt lại.
// 100% = cỡ chữ gốc của editor lúc khởi tạo; mức hiện tại = fontInfo.fontSize (đã gồm zoom) — Monaco giữ zoom chung cho mọi editor.
(function () {
  let ed = null, bar = null, input = null, base = 15, lastText = '100%';
  const watch = new MutationObserver(() => place());   // toolbar / thanh trên cùng đổi style-class → ẩn/hiện, đặt lại ô

  const percent = () => Math.round((ed.getOption(monaco.editor.EditorOption.fontInfo).fontSize || base) / base * 100);

  // Đặt ô đè lên đầu bên phải toolbar, căn theo HÀNG ĐẦU của thanh (nút đầu tiên) và chừa chỗ ở thanh để không đè nút.
  // Toolbar đang ẩn (Settings → Bố cục / ẩn nhanh, hoặc "Tự ẩn Menu/Toolbar" lúc chưa rê chuột lên mép trên) thì ẩn luôn ô này —
  // trước đây ô rơi về góc phải trên editor và đè lên hộp tìm (Ctrl+F) của Monaco.
  function place() {
    if (!bar) return;
    const tb = document.querySelector('.shToolbar');
    if (tb && !tb.dataset.zoomWatch) { tb.dataset.zoomWatch = '1'; watch.observe(tb, { attributes: true, attributeFilter: ['style'] }); }
    const r = tb && tb.offsetParent !== null ? tb.getBoundingClientRect() : null;
    if (!r || r.height <= 0 || r.bottom <= 0 || r.top >= window.innerHeight) { bar.style.display = 'none'; return; }
    bar.style.display = 'flex';
    // "Tự ẩn": thanh trên cùng nổi (z-index 15000) khi rê chuột lên mép trên → ô phải nổi trên nó, vẫn dưới menu (21000).
    bar.style.zIndex = document.body.classList.contains('shAutoHide') && tb.closest('#shellTop') ? '15001' : '60';
    if (!tb.dataset.zoomPad) { tb.dataset.zoomPad = '1'; tb.style.boxSizing = 'border-box'; tb.style.paddingRight = (bar.offsetWidth + 16) + 'px'; }
    const first = tb.querySelector('button, .shTbBtn');
    const fr = first ? first.getBoundingClientRect() : r;
    bar.style.top = Math.max(0, fr.top + (fr.height - bar.offsetHeight) / 2) + 'px';
    bar.style.right = Math.max(4, window.innerWidth - r.right + 10) + 'px';
  }

  function refresh() {
    if (!ed || !bar) return;
    lastText = percent() + '%';
    if (document.activeElement !== input) input.value = lastText;   // đang gõ thì không ghi đè
    place();
  }

  const act = (id) => { ed.trigger('zoombar', id, null); setTimeout(refresh, 40); };

  // Đặt cỡ chữ theo % cụ thể: bỏ zoom của Monaco rồi đặt fontSize = cỡ gốc × %.
  function setPercent(pct) {
    if (!(pct > 0)) return;
    pct = Math.max(30, Math.min(500, pct));
    ed.trigger('zoombar', 'editor.action.fontZoomReset', null);
    const size = Math.round(base * pct / 10) / 10;
    ed.updateOptions({ fontSize: size });
    const v = window.bcodeViewer;
    if (v && v.editorSecondary) v.editorSecondary.updateOptions({ fontSize: size });
    setTimeout(refresh, 40);
  }

  function build() {
    bar = document.createElement('div');
    bar.id = 'zoomBar';
    bar.style.cssText = 'position:fixed;z-index:60;display:flex;align-items:center;gap:6px;padding:2px 6px 2px 6px;border-radius:8px;'
      + "font:12.5px 'Segoe UI',sans-serif;color:var(--bc-text,#ddd);background:var(--bc-panel-alt,#2d2d30);border:1px solid var(--bc-border,#555);"
      + 'user-select:none';
    const mk = (txt, title, fn, accent) => {
      const b = document.createElement('button');
      b.textContent = txt; b.title = title;
      b.style.cssText = 'font:inherit;color:' + (accent ? 'var(--bc-accent-hover,#1177bb)' : 'inherit') + ';background:transparent;border:1px solid '
        + (accent ? 'var(--bc-accent,#0e639c)' : 'transparent') + ';border-radius:5px;padding:1px 9px;cursor:pointer';
      b.onmouseenter = () => { b.style.background = 'rgba(128,128,128,.25)'; };
      b.onmouseleave = () => { b.style.background = 'transparent'; };
      b.onclick = fn;
      return b;
    };
    input = document.createElement('input');
    input.type = 'text'; input.value = '100%'; input.spellcheck = false; input.autocomplete = 'off';
    input.title = 'Gõ số % rồi Enter (30–500)';
    input.style.cssText = 'width:52px;font:inherit;text-align:center;padding:1px 4px;color:inherit;background:var(--bc-input,#3c3c3c);border:1px solid var(--bc-border,#555);border-radius:5px;outline:none';
    const apply = () => setPercent(parseFloat(String(input.value).replace(',', '.').replace('%', '')));
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { apply(); input.blur(); }
      else if (e.key === 'Escape') { input.value = lastText; input.blur(); }
      e.stopPropagation();   // phím gõ trong ô này không được rơi vào phím tắt của editor / toolbar
    });
    input.addEventListener('focus', () => input.select());
    input.addEventListener('blur', () => { if (input.value !== lastText) apply(); });
    bar.append(input,
      mk('−', 'Thu nhỏ chữ', () => act('editor.action.fontZoomOut')),
      mk('+', 'Phóng to chữ', () => act('editor.action.fontZoomIn')),
      mk('Đặt lại', 'Về cỡ chữ gốc (100%)', () => setPercent(100), true));
    document.body.appendChild(bar);
  }

  window.bcodeZoomBar = {
    init(editor) {
      ed = editor;
      base = (ed.getRawOptions && ed.getRawOptions().fontSize) || 15;   // 100% = cỡ gốc lúc khởi tạo
      if (!bar) build();
      refresh();
      // Cập nhật mỗi khi cỡ chữ hiệu dụng đổi (lăn chuột, nút, gõ %, đổi theme/font) và khi toolbar đổi chỗ / dựng lại.
      ed.onDidChangeConfiguration((e) => { if (e.hasChanged(monaco.editor.EditorOption.fontInfo)) refresh(); });
      window.addEventListener('resize', place);
      window.addEventListener('wheel', (e) => { if (e.ctrlKey) setTimeout(refresh, 40); }, { capture: true, passive: true });
      setInterval(place, 1000);   // toolbar được dựng lại / đổi vị trí (Settings → Bố cục) → đặt lại ô
      // Ẩn/hiện toolbar, "Tự ẩn" trượt thanh trên cùng ra/vào → ẩn/hiện ô ngay, không chờ nhịp 1 giây ở trên.
      const top = document.getElementById('shellTop');
      if (top) { watch.observe(top, { attributes: true, attributeFilter: ['class', 'style'] }); top.addEventListener('transitionend', place); }
    },
    show: refresh,
  };
})();
