// Ô phần trăm phóng to chữ — LUÔN hiện ở đầu bên phải toolbar, ngang hàng với các nút: "[150%]  −  +  Đặt lại".
// Ctrl + lăn chuột (zoom có sẵn của Monaco) đổi cỡ chữ; ô này cho biết đang ở mức nào, gõ số % cụ thể + Enter để đặt, hoặc dùng − / + / Đặt lại.
// 100% = cỡ chữ gốc của editor lúc khởi tạo; mức hiện tại = fontInfo.fontSize (đã gồm zoom) — Monaco giữ zoom chung cho mọi editor.
(function () {
  let ed = null, bar = null, input = null, base = 15, lastText = '100%';

  const percent = () => Math.round((ed.getOption(monaco.editor.EditorOption.fontInfo).fontSize || base) / base * 100);

  // Đặt ô đè lên đầu bên phải toolbar, căn theo HÀNG ĐẦU của thanh (nút đầu tiên) và chừa chỗ ở thanh để không đè nút;
  // không có toolbar thì rơi về góc phải trên editor.
  function place() {
    if (!bar) return;
    const tb = document.querySelector('.shToolbar');
    const r = tb && tb.offsetParent !== null ? tb.getBoundingClientRect() : null;
    if (r && r.height > 0) {
      if (!tb.dataset.zoomPad) { tb.dataset.zoomPad = '1'; tb.style.boxSizing = 'border-box'; tb.style.paddingRight = (bar.offsetWidth + 16) + 'px'; }
      const first = tb.querySelector('button, .shTbBtn');
      const fr = first ? first.getBoundingClientRect() : r;
      bar.style.top = Math.max(0, fr.top + (fr.height - bar.offsetHeight) / 2) + 'px';
      bar.style.right = Math.max(4, window.innerWidth - r.right + 10) + 'px';
    } else {
      const area = document.getElementById('editorContainer');
      const ar = area ? area.getBoundingClientRect() : { top: 40, right: window.innerWidth };
      bar.style.top = Math.max(4, ar.top + 6) + 'px';
      bar.style.right = Math.max(4, window.innerWidth - ar.right + 26) + 'px';
    }
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
    },
    show: refresh,
  };
})();
