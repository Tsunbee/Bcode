// Hiển thị nội dung yêu cầu "đẹp" như cửa sổ xem yêu cầu của FBO: tiêu đề menu (màu theo cấp), nội dung, khối ghi chú (tester / lỗi) tô màu theo tag.
// Dùng chung cho Note (New) (advnote.html) và Báo cáo yêu cầu FSG (fsgreq.html). Chỉ dựng HTML từ chữ thuần (đã escape) — không ghi gì vào dữ liệu.
(function () {
  var esc = function (s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); };

  var css = '' +
    '.rr{font:14px/1.55 "Segoe UI",Tahoma,sans-serif;color:var(--text)}' +
    '.rr-head{display:flex;align-items:baseline;gap:.5em;flex-wrap:wrap;padding:.55em .8em;border-radius:6px;border:1px solid var(--border);background:color-mix(in srgb,var(--accent) 10%,var(--panel-alt,var(--panel)))}' +
    '.rr-proj{color:#d9904f;font-weight:700}' +
    '.rr-par{font-weight:700;color:var(--text)}' +
    '.rr-sep{color:var(--text-muted)}' +
    '.rr-last{color:#5aa9ff;font-weight:700}' +
    '.rr-tail{margin-left:auto;color:var(--text-muted);font-weight:600;white-space:nowrap}' +
    '.rr-body{padding:.7em .3em;white-space:pre-wrap;word-break:break-word}' +
    '.rr-notes{margin-top:.4em;padding:.5em .8em;border-radius:6px;border-left:3px solid #4a7fd0;background:color-mix(in srgb,var(--text) 7%,transparent);font-size:.88em;line-height:1.55;word-break:break-word}' +
    '.rr-notes .rr-nt{font-weight:600;color:var(--text-muted);margin-bottom:.15em}' +
    '.rr-note .t1{color:#6fa0e8;font-weight:600}' +
    '.rr-note .t2{color:#e0884f;font-weight:600}' +
    '.rr-note .lt{color:#58b27a}' +
    '.rr-info{margin-top:.6em;padding-top:.35em;border-top:1px dashed var(--border);color:var(--text-muted);font-size:.85em}' +
    'html[data-theme="light"] .rr-proj{color:#8a4b12}html[data-theme="light"] .rr-last{color:#0b3fd0}' +
    'html[data-theme="light"] .rr-note .t1{color:#0047ab}html[data-theme="light"] .rr-note .t2{color:#b3501a}html[data-theme="light"] .rr-note .lt{color:#1d7a43}';
  var st = document.createElement('style'); st.id = 'rrCss'; st.textContent = css; document.head.appendChild(st);

  /// "[VPMILK] Bán Hàng\Hóa Đơn Bán Hàng [922] [VIETHB]" → mã dự án nâu, các cấp cha đậm, cấp cuối xanh, cụm [mã y/c] [người] xám bên phải.
  function menuHtml(raw) {
    var s = String(raw || '').trim(); if (!s) return '';
    var proj = '', tail = '', m = /^\[([^\]]+)\]\s*/.exec(s);
    if (m) { proj = m[1]; s = s.slice(m[0].length); }
    m = /((?:\s*\[[^\]]+\])+)\s*$/.exec(s);
    if (m) { tail = m[1].trim(); s = s.slice(0, m.index).trim(); }
    var parts = s.split(/\\/).map(function (x) { return x.trim(); }).filter(Boolean);
    var path = parts.map(function (p, i) { return '<span class="' + (i === parts.length - 1 ? 'rr-last' : 'rr-par') + '">' + esc(p) + '</span>'; }).join('<span class="rr-sep">\\</span>');
    return '<div class="rr-head">' + (proj ? '<span class="rr-proj">[' + esc(proj) + ']</span>' : '') + (path ? '<span class="rr-path">' + path + '</span>' : '') +
      (tail ? '<span class="rr-tail">' + esc(tail) + '</span>' : '') + '</div>';
  }

  /// Mỗi dòng ghi chú: "[tester]:nội dung" hoặc "[tester][trạng thái]:nội dung [LT:...]" → tag tô màu.
  function notesHtml(text) {
    var lines = String(text || '').replace(/\r/g, '').split('\n').filter(function (l) { return l.trim().length; });
    if (!lines.length) return '';
    return '<div class="rr-notes">' + lines.map(function (l) {
      var m = /^\s*(\[[^\]]*\])(\[[^\]]*\])?\s*:?\s*([\s\S]*)$/.exec(l);
      if (!m) return '<div class="rr-note">' + esc(l) + '</div>';
      var rest = esc(m[3]).replace(/(\[LT:[^\]]*\])/g, '<span class="lt">$1</span>');
      return '<div class="rr-note"><span class="t1">' + esc(m[1]) + '</span>' + (m[2] ? '<span class="t2">' + esc(m[2]) + '</span>' : '') + ': ' + rest + '</div>';
    }).join('') + '</div>';
  }

  /// o = { menu, body, notes, info } (chữ thuần) → HTML.
  function build(o) {
    o = o || {};
    return '<div class="rr">' + menuHtml(o.menu) +
      (o.body ? '<div class="rr-body">' + esc(o.body) + '</div>' : '') +
      notesHtml(o.notes) +
      (o.info ? '<div class="rr-info">' + esc(o.info) + '</div>' : '') + '</div>';
  }

  /// Tách nội dung y/c do "Sync yêu cầu" dựng (Menu: … / nội dung / Ghi chú: … / --- Bộ phận: …) thành các phần; chữ tự gõ thì cả đoạn là body.
  function parse(text) {
    var blocks = String(text || '').replace(/\r/g, '').split(/\n[ \t]*\n/);
    var o = { menu: '', body: [], notes: '', info: '' };
    blocks.forEach(function (b, i) {
      var t = b.replace(/^\n+|\n+$/g, '');
      if (!t.trim()) return;
      var m;
      if (i === 0 && (m = /^\s*Menu:\s*([\s\S]*)$/.exec(t))) { o.menu = m[1].trim(); return; }
      if ((m = /^\s*Ghi chú:\s*([\s\S]*)$/.exec(t))) { o.notes = m[1].trim(); return; }
      if ((m = /^\s*---\s*\n\s*(Bộ phận:[\s\S]*)$/.exec(t))) { o.info = m[1].trim(); return; }
      o.body.push(t);
    });
    o.body = o.body.join('\n\n');
    return o;
  }

  window.RichReq = { build: build, parse: parse, menuHtml: menuHtml, notesHtml: notesHtml, esc: esc };
})();
