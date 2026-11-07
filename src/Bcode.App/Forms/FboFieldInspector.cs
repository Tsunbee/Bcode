using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Bcode.App.Forms;

/// <summary>
/// Tab FSG FBO (Ctrl+4): xem tên field / bảng / file filter ngay trên trang FBO — như extension Chrome "Tiện ích FBO" của FSD
/// (FBOExtention/RequestPage.js), nhưng chỉ lấy phần xem cấu trúc, không có tự đăng nhập / tài khoản / quyền debugger.
/// Tên nằm sẵn trong id thẻ ASP.NET của FBO nên không gọi gì lên server:
///   …HeaderText_&lt;field&gt; / …dirExtender_form_&lt;field&gt; / …searchExtender_form_&lt;field&gt; / …formExtender&lt;x&gt;_form_&lt;field&gt; → tên field
///   …_FastBusiness_MainReport_dirExtender_FormGrid&lt;bảng&gt;_Wait… → tên bảng của grid
///   …_formExtender&lt;tên&gt;_updateDlgTitle → tên file filter
/// Rê chuột: tooltip hiện tên (giữ tooltip gốc ở dòng dưới). Shift+click: copy tên. Ô bị khoá (disabled — không nhận click):
/// rê chuột giữ yên 1 giây là copy. Rê vào form chi tiết (dir) thì hiện stt_rec ở dưới tiêu đề form.
/// Copy: trang chính gửi tên về Bcode (chrome.webview.postMessage → Clipboard.SetText) để không làm mất focus ô đang nhập;
/// trong iframe (không có chrome.webview) thì dùng sự kiện copy của trình duyệt.
/// </summary>
internal static class FboFieldInspector
{
    private const string CopyTag = "bcode-fbo-copy";

    /// <param name="onSaved">Người dùng bấm "Lưu" trên form FBO: (URL trang, JSON {master, rows} đọc trước khi FBO xử lý) — giữ làm mẫu "Lần lưu gần nhất".</param>
    public static async Task AttachAsync(CoreWebView2 core, Action<string, string>? onSaved = null)
    {
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Script);
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t)) return;
                if (t.GetString() == "bcode-fbo-saved")
                {
                    if (onSaved is not null && root.TryGetProperty("data", out var data) && data.GetString() is { Length: > 2 and < 2_000_000 } json)
                        onSaved(root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "", json);
                    return;
                }
                if (t.GetString() != CopyTag
                    || !root.TryGetProperty("text", out var v) || v.GetString() is not { Length: > 0 and <= 300 } text) return;
                Clipboard.SetText(text);
            }
            catch { /* tin nhắn khác / clipboard đang bận — bỏ qua */ }
        };
    }

    private const string Script = """
(function () {
  if (window.__bcodeFboField) return;
  window.__bcodeFboField = true;
  // Đếm yêu cầu AJAX đang chạy của trang (XMLHttpRequest + fetch) — Điền mẫu điền xong 1 ô thì CHỜ FBO tra mã / nạp tên, đvt, giá xong
  // (không còn yêu cầu nào) rồi mới sang ô kế, không thì ô sau bị FBO ghi đè / bỏ (người dùng báo: dán hết một lượt → lỗi).
  window.__bcodeInflight = 0;
  try {
    var XS = window.XMLHttpRequest && window.XMLHttpRequest.prototype.send;
    if (XS && !window.XMLHttpRequest.prototype.__bcodeWrapped) {
      window.XMLHttpRequest.prototype.send = function () {
        var done = false, self = this;
        function fin() { if (!done) { done = true; window.__bcodeInflight = Math.max(0, window.__bcodeInflight - 1); } }
        window.__bcodeInflight++;
        try { self.addEventListener('loadend', fin); } catch (e) { }
        try { return XS.apply(self, arguments); } catch (e) { fin(); throw e; }
      };
      window.XMLHttpRequest.prototype.__bcodeWrapped = true;
    }
    var F = window.fetch;
    if (F && !F.__bcodeWrapped) {
      var wf = function () {
        window.__bcodeInflight++;
        var p;
        try { p = F.apply(this, arguments); } catch (e) { window.__bcodeInflight--; throw e; }
        return p.then(function (r) { window.__bcodeInflight = Math.max(0, window.__bcodeInflight - 1); return r; },
                      function (e) { window.__bcodeInflight = Math.max(0, window.__bcodeInflight - 1); throw e; });
      };
      wf.__bcodeWrapped = true;
      window.fetch = wf;
    }
  } catch (e) { }
  var FIELD = /(?:HeaderText_)(.+)$|(?:dirExtender_form_)(.+)$|(?:searchExtender_form_)(.+)$|(?:formExtender\w+_form_)(.+)$/;
  var GRID = /_FastBusiness_MainReport_dirExtender_FormGrid(.+?)_Wait/;
  var FILTER = /_formExtender(.+?)_updateDlgTitle$/;
  var DIR = 'ctl00_FastBusiness_MainReport_dirExtender_';
  var hoverTimer = 0;

  function find(el) {
    for (var i = 0; el && el.nodeType === 1 && i < 3; i++, el = el.parentElement) {
      var id = el.id, m;
      if (!id) continue;
      if ((m = id.match(FIELD))) return { el: el, text: m[1] || m[2] || m[3] || m[4] };
      if ((m = id.match(GRID))) return { el: el, text: m[1] };
      if ((m = id.match(FILTER))) return { el: el, text: m[1] };
    }
    return null;
  }

  function toast(text) {
    var old = document.getElementById('bcodeFboCopyToast');
    if (old) old.remove();
    var d = document.createElement('div');
    d.id = 'bcodeFboCopyToast';
    d.textContent = 'Đã copy: ' + text;
    d.style.cssText = 'position:fixed;top:30px;left:0;z-index:2147483647;background:#0095ff;color:#fff;padding:10px 14px;' +
      'border-radius:0 8px 8px 0;font:13px Arial,sans-serif;box-shadow:0 4px 8px rgba(0,0,0,.2);pointer-events:none';
    (document.body || document.documentElement).appendChild(d);
    setTimeout(function () { d.remove(); }, 1800);
  }

  function copy(text) {
    var host = window.chrome && window.chrome.webview;
    if (host && window === window.top) {
      host.postMessage({ type: 'bcode-fbo-copy', text: text });
      toast(text);
      return;
    }
    var ok = false;
    function onCopy(e) { e.clipboardData.setData('text/plain', text); e.preventDefault(); ok = true; }
    document.addEventListener('copy', onCopy, true);
    try { document.execCommand('copy'); } catch (e) { }
    document.removeEventListener('copy', onCopy, true);
    if (ok) toast(text);
  }

  function showSttRec(e) {
    var dlg = document.getElementById(DIR + 'dlgModalPopupBehavior_foregroundElement');
    var ref = document.getElementById(DIR + 'Wait');
    var src = document.getElementById(DIR + 'form_stt_rec');
    if (!dlg || !ref || !src || !ref.parentNode) return;
    var r = dlg.getBoundingClientRect();
    if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) return;
    var box = document.getElementById('div_FBOExtention_Value');
    if (!box) {
      box = document.createElement('div');
      box.id = 'div_FBOExtention_Value';
      ref.parentNode.insertBefore(box, ref.nextSibling);
    }
    if (box.textContent !== src.value) box.textContent = src.value;
  }

  document.addEventListener('mouseover', function (e) {
    try { showSttRec(e); } catch (x) { }
    var f = find(e.target);
    if (!f) return;
    var el = f.el;
    if (el.__bcodeTitle === undefined) el.__bcodeTitle = el.getAttribute('title') || '';
    var tip = f.text + (el.disabled ? ' (giữ chuột 1 giây: copy)' : ' (Shift+click: copy)');
    el.title = el.__bcodeTitle && el.__bcodeTitle !== tip ? tip + '\n' + el.__bcodeTitle : tip;
    clearTimeout(hoverTimer);
    if (el.disabled) hoverTimer = setTimeout(function () { copy(f.text); }, 1000);
  }, true);

  document.addEventListener('mouseout', function () { clearTimeout(hoverTimer); }, true);

  document.addEventListener('click', function (e) {
    if (!e.shiftKey) return;
    var f = find(e.target);
    if (f) copy(f.text);
  }, true);

  // ---- Hiện TÊN MỌI FIELD cùng lúc (nút "🏷 Tên field" trên thanh Bcode) — nhãn vàng nhỏ trên từng ô / tiêu đề cột ----
  // Mỗi frame tự vẽ nhãn trong document của nó (lớp position:fixed, không bắt chuột); trang chính bật/tắt cho mọi iframe cùng nguồn.
  var showOn = false, layer = null, timer = 0;
  var INPUTS = { INPUT: 1, SELECT: 1, TEXTAREA: 1 };
  function nameOf(id) {
    var m = id.match(FIELD); if (m) return { name: m[1] || m[2] || m[3] || m[4], head: !!m[1] };
    if ((m = id.match(GRID))) return { name: 'bảng ' + m[1], head: true };
    return null;
  }
  function draw() {
    timer = 0;
    if (!showOn) { if (layer) { layer.remove(); layer = null; } return; }
    if (!layer) {
      layer = document.createElement('div');
      layer.id = 'bcodeFieldLayer';
      layer.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;z-index:2147483646;pointer-events:none';
    }
    if (!layer.isConnected) (document.body || document.documentElement).appendChild(layer);
    var vw = window.innerWidth, vh = window.innerHeight, out = [], seen = {}, placed = [];
    // Ô có thật sự nhìn thấy không: điểm giữa (và mép trái) của ô phải trúng chính nó — ô nằm SAU hộp thoại / lớp mờ, hoặc cột lưới
    // đã cuộn ra ngoài khung (điểm rơi vào thứ khác) thì không vẽ nhãn, khỏi lòi nhãn ra ngoài hộp thoại.
    function visible(el, r) {
      var pts = [[r.left + r.width / 2, r.top + r.height / 2], [r.left + Math.min(6, r.width / 2), r.top + r.height / 2]];
      for (var k = 0; k < pts.length; k++) {
        var x = pts[k][0], y = pts[k][1];
        if (x < 0 || y < 0 || x >= vw || y >= vh) continue;
        var hit = document.elementFromPoint(x, y);
        if (!hit) continue;
        if (hit === el || el.contains(hit)) return true;
        // trúng khung bao gần (vd. ô nhập readonly nằm trong td/div bọc): chấp nhận nếu chỉ cách 3 cấp
        for (var p = el.parentElement, d = 0; p && d < 3; p = p.parentElement, d++) if (p === hit) return true;
      }
      return false;
    }
    function overlaps(a) {
      for (var k = 0; k < placed.length; k++) {
        var b = placed[k];
        if (a.l < b.r && a.r > b.l && a.t < b.b && a.b > b.t) return true;
      }
      return false;
    }
    var all = document.querySelectorAll('[id]');
    for (var i = 0; i < all.length && out.length < 600; i++) {
      var el = all[i];
      if (el === layer || el.id.indexOf('bcode') === 0) continue;
      var n = nameOf(el.id);
      if (!n) continue;
      // Khung bao ngoài 1 ô nhập (cùng tên) thì để nhãn cho chính ô nhập.
      if (!INPUTS[el.tagName] && !n.head && el.querySelector('input,select,textarea')) continue;
      var r = el.getBoundingClientRect();
      if (r.width < 2 || r.height < 2 || r.bottom < 0 || r.right < 0 || r.top > vh || r.left > vw) continue;
      var key = n.name + '|' + Math.round(r.left / 4) + '|' + Math.round(r.top / 4);
      if (seen[key]) continue;
      if (!visible(el, r)) continue;
      seen[key] = 1;
      // Chỗ đặt nhãn: ngay trên mép trái ô → trên mép phải → góc trong trên-phải → dưới mép trái; chỗ nào không đè nhãn đã đặt thì lấy.
      var w = n.name.length * 5.4 + 7, h = 11;
      var cands = [[r.left, r.top - h], [r.right - w, r.top - h], [r.right - w - 1, r.top + 1], [r.left, r.bottom]];
      var pos = null;
      for (var c = 0; c < cands.length && !pos; c++) {
        var L = Math.max(0, Math.min(vw - w, cands[c][0])), T = Math.max(0, Math.min(vh - h, cands[c][1]));
        var box = { l: L, t: T, r: L + w, b: T + h };
        if (!overlaps(box)) pos = box;
      }
      if (!pos) continue;   // chỗ nào cũng đè nhãn khác — bỏ, rê chuột vào ô vẫn xem / chép được tên
      placed.push(pos);
      out.push('<span style="position:fixed;left:' + Math.round(pos.l) + 'px;top:' + Math.round(pos.t) + 'px;' +
        'font:600 9px/11px Consolas,\'Cascadia Mono\',monospace;padding:0 3px;border-radius:2px;white-space:nowrap;' +
        'box-shadow:0 1px 2px rgba(0,0,0,.18);' +
        (n.head ? 'background:rgba(11,92,173,.88);color:#fff' : 'background:rgba(255,255,255,.94);color:#0b5cad;border:1px solid rgba(11,92,173,.45)') + '">' +
        n.name.replace(/[&<>]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]; }) + '</span>');
    }
    layer.innerHTML = out.join('');
  }
  function schedule() { if (showOn && !timer) timer = setTimeout(draw, 120); }
  new MutationObserver(function (list) {
    if (!showOn) return;
    for (var i = 0; i < list.length; i++) if (!layer || !layer.contains(list[i].target)) { schedule(); return; }
  }).observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ['style', 'class'] });
  window.addEventListener('scroll', schedule, true);
  window.addEventListener('resize', schedule);
  function frames(win, fn) {
    fn(win);
    for (var i = 0; i < win.frames.length; i++) { try { if (win.frames[i].document) frames(win.frames[i], fn); } catch (e) { } }
  }
  // Bật / tắt ở frame này (gọi từ trang chính cho mọi frame).
  window.__bcodeFieldsLocal = function (on) { showOn = !!on; clearTimeout(timer); timer = 0; draw(); };
  window.__bcodeShowFields = function (on) {
    try { localStorage.setItem('bcodeShowFields', on ? '1' : '0'); } catch (e) { }
    frames(window, function (w) { try { w.__bcodeFieldsLocal && w.__bcodeFieldsLocal(on); } catch (e) { } });
    return on;
  };
  // Frame mới mở (menu / dialog trong iframe) tự theo trạng thái đang bật.
  try { if (localStorage.getItem('bcodeShowFields') === '1') setTimeout(function () { window.__bcodeFieldsLocal(true); }, 300); } catch (e) { }
  window.addEventListener('storage', function (e) { if (e.key === 'bcodeShowFields') window.__bcodeFieldsLocal(e.newValue === '1'); });

  // ---- Điền dữ liệu mẫu (nút "🎲 Điền mẫu"): Bcode lấy danh sách ô của form đang mở, tra database rồi gửi giá trị về ----
  var FORMFIELD = /(?:dirExtender_form_|searchExtender_form_|formExtender\w+_form_)(.+)$/;
  window.__bcodeFormFields = function () {
    var res = [], n = 0;
    frames(window, function (w) {
      var all;
      try { all = w.document.querySelectorAll('input,select,textarea'); } catch (e) { return; }
      for (var i = 0; i < all.length; i++) {
        var el = all[i], m = el.id && el.id.match(FORMFIELD);
        if (!m) continue;
        var t = (el.type || '').toLowerCase();
        if (t === 'hidden' || t === 'button' || t === 'submit' || t === 'image' || t === 'file') continue;
        var r = el.getBoundingClientRect();
        if (r.width < 2 || r.height < 2) continue;   // ô ở tab đang ẩn / form đang đóng
        var tag = 'f' + (++n);
        el.setAttribute('data-bcode-fill', tag);
        res.push({ tag: tag, field: m[1], kind: el.tagName === 'SELECT' ? 'select' : (t === 'checkbox' ? 'check' : 'text'),
          value: t === 'checkbox' ? (el.checked ? '1' : '') : (el.value || ''), disabled: !!el.disabled, readOnly: !!el.readOnly,
          maxLength: el.maxLength > 0 ? el.maxLength : 0 });
      }
      // Lưới chi tiết: ô của lưới FBO chỉ thành ô nhập khi bấm vào → lấy theo tiêu đề cột (HeaderText_x) đang nhìn thấy, ô dòng 1 ngay dưới.
      var heads;
      try { heads = w.document.querySelectorAll('[id*="HeaderText_"]'); } catch (e) { return; }
      var gvw = w.innerWidth, gvh = w.innerHeight;
      for (var j = 0; j < heads.length; j++) {
        var hd = heads[j], hm = hd.id.match(/HeaderText_(.+)$/);
        if (!hm) continue;
        var hr = hd.getBoundingClientRect();
        if (hr.width < 4 || hr.height < 4 || hr.right < 0 || hr.left > gvw || hr.top < 0 || hr.bottom > gvh) continue;
        var cx = hr.left + hr.width / 2, cy = hr.top + hr.height / 2, hit = w.document.elementFromPoint(cx, cy);
        if (!hit || !(hit === hd || hd.contains(hit) || hit.contains(hd))) continue;     // tiêu đề bị che (lưới danh sách sau hộp thoại)
        // Lưới chưa được bấm vào lần nào có thể chưa vẽ dòng 1 → vẫn nhận cột (giá trị rỗng); lúc điền sẽ kích hoạt lưới (fillGridCell).
        var cell = w.document.elementFromPoint(cx, hr.bottom + 10);
        var hasCell = cell && !hd.contains(cell) && !cell.contains(hd) && cell.closest && cell.closest('tr');
        var gtag = 'g' + (++n);
        hd.setAttribute('data-bcode-fill', gtag);
        res.push({ tag: gtag, field: hm[1], kind: 'grid', value: hasCell ? (cell.textContent || '').trim() : '', disabled: false, readOnly: false, maxLength: 0 });
      }
    });
    return JSON.stringify(res);
  };
  function sleep(ms) { return new Promise(function (ok) { setTimeout(ok, ms); }); }
  // Chờ trang "yên": đợi tối thiểu minMs, rồi tới khi MỌI frame không còn yêu cầu AJAX nào trong 250ms liền (tối đa maxMs) + 120ms cho FBO vẽ lại.
  async function waitIdle(minMs, maxMs) {
    var t0 = Date.now(), quiet = 0;
    await sleep(minMs);
    while (Date.now() - t0 < maxMs) {
      var n = 0;
      frames(window.top || window, function (w) { try { n += w.__bcodeInflight || 0; } catch (e) { } });
      if (n === 0) { quiet += 50; if (quiet >= 250) break; } else quiet = 0;
      await sleep(50);
    }
    await sleep(120);
  }
  // Toạ độ y giữa dòng thứ row (0 = dòng 1) của lưới dưới tiêu đề cột hd: theo các <tr> anh em của dòng 1, không có <tr> thì theo chiều cao dòng 1.
  function rowCenterY(w, hd, row) {
    var d = w.document, hr = hd.getBoundingClientRect(), x = hr.left + hr.width / 2;
    var first = d.elementFromPoint(x, hr.bottom + 10);
    if (!first || hd.contains(first)) return -1;
    var tr = first.closest && first.closest('tr');
    if (tr && tr.parentElement) {
      var rows = Array.prototype.filter.call(tr.parentElement.children, function (r) { return r.tagName === 'TR' && r.getBoundingClientRect().height > 2; });
      var i0 = rows.indexOf(tr);
      var r = rows[i0 + row];
      if (!r) return -1;
      var rr = r.getBoundingClientRect();
      return rr.top + rr.height / 2;
    }
    // chưa có dòng nào (lưới chưa kích hoạt): dòng 1 = ngay dưới tiêu đề; dòng sau thì chưa có
    return row === 0 ? hr.bottom + 10 : -1;
  }
  // Chuỗi sự kiện như 1 cú bấm chuột thật (pointer + mouse + focus) — lưới FBO chỉ mở ô nhập khi đã được "kích hoạt" bằng cú bấm thật.
  function realClick(w, el, x, y, dbl) {
    var o = { bubbles: true, cancelable: true, view: w, clientX: x, clientY: y, button: 0, buttons: 1, pointerId: 1, pointerType: 'mouse', isPrimary: true };
    function ev(T, t) { try { el.dispatchEvent(new T(t, o)); } catch (e) { } }
    var P = w.PointerEvent || w.MouseEvent;
    ev(P, 'pointerover'); ev(w.MouseEvent, 'mouseover'); ev(P, 'pointerdown'); ev(w.MouseEvent, 'mousedown');
    try { (el.closest && el.closest('[tabindex]') || el).focus({ preventScroll: true }); } catch (e) { }
    ev(P, 'pointerup'); ev(w.MouseEvent, 'mouseup'); ev(w.MouseEvent, 'click');
    if (dbl) ev(w.MouseEvent, 'dblclick');
  }
  // Ô nhập FBO vừa mở ở (x, y): thành phần đang focus, hoặc INPUT/TEXTAREA nằm đúng chỗ bấm (FBO có lúc mở ô nhập mà không chuyển focus).
  function editorAt(w, x, y, before) {
    var d = w.document, a = d.activeElement;
    if (a && a !== before && /^(INPUT|TEXTAREA)$/.test(a.tagName)) return a;
    var p = d.elementFromPoint(x, y);
    if (p && /^(INPUT|TEXTAREA)$/.test(p.tagName) && p.type !== 'checkbox') return p;
    if (p && p.querySelector) { var q = p.querySelector('input:not([type=hidden]):not([type=checkbox]),textarea'); if (q && q.getBoundingClientRect().width > 2) return q; }
    return null;
  }
  // Nút "Thêm" (thêm dòng) của lưới gần tiêu đề cột hd — dùng khi cần dòng chi tiết thứ 2 trở đi mà lưới chưa có.
  function clickAddRow(w, hd) {
    var d = w.document, hr = hd.getBoundingClientRect(), best = null, bestDy = 1e9;
    var all = d.querySelectorAll('a,span,div,button,td');
    for (var i = 0; i < all.length; i++) {
      var el = all[i];
      if (el.children.length > 2 || (el.textContent || '').trim() !== 'Thêm') continue;
      var r = el.getBoundingClientRect();
      if (r.width < 2 || r.bottom > hr.top + 4) continue;   // nút nằm TRÊN lưới
      var dy = hr.top - r.bottom;
      if (dy < bestDy) { bestDy = dy; best = el; }
    }
    if (!best) return false;
    try { best.click(); } catch (e) { return false; }
    return true;
  }
  // Điền 1 ô lưới: bấm vào ô dòng (row) dưới tiêu đề cột → FBO mở ô nhập (thành phần đang focus) → gõ giá trị → Tab để FBO nhận và tra mã.
  async function fillGridCell(w, hd, value, row) {
    row = row || 0;
    var d = w.document, hr = hd.getBoundingClientRect();
    var x = hr.left + hr.width / 2, y = rowCenterY(w, hd, row);
    if (y < 0 && row > 0 && clickAddRow(w, hd)) { await sleep(350); y = rowCenterY(w, hd, row); }
    if (y < 0) return false;
    var cell = d.elementFromPoint(x, y);
    if (!cell || hd.contains(cell)) return false;
    var before = d.activeElement;
    // 1) bấm thường → 2) bấm đúp → 3) lưới chưa kích hoạt: bấm vào thân lưới (như người dùng click vào chi tiết), đợi FBO vẽ dòng, bấm lại ô
    realClick(w, cell, x, y, false);
    await sleep(220);
    var ed = editorAt(w, x, y, before);
    if (!ed) { realClick(w, d.elementFromPoint(x, y) || cell, x, y, true); await sleep(250); ed = editorAt(w, x, y, before); }
    if (!ed) {
      realClick(w, cell, x, y, false);
      await sleep(450);
      var y2 = rowCenterY(w, hd, row);
      if (y2 >= 0) { y = y2; var c2 = d.elementFromPoint(x, y); if (c2 && !hd.contains(c2)) { realClick(w, c2, x, y, false); await sleep(250); ed = editorAt(w, x, y, before); if (!ed) { realClick(w, c2, x, y, true); await sleep(250); ed = editorAt(w, x, y, before); } } }
    }
    if (!ed || ed.disabled || ed.readOnly) return false;
    try { ed.focus({ preventScroll: true }); } catch (e) { }
    ed.value = value;
    ['input', 'change', 'keyup'].forEach(function (t) { try { ed.dispatchEvent(new w.Event(t, { bubbles: true })); } catch (e) { } });
    try { ed.dispatchEvent(new w.KeyboardEvent('keydown', { key: 'Tab', code: 'Tab', keyCode: 9, which: 9, bubbles: true, cancelable: true })); } catch (e) { }
    try { ed.blur(); } catch (e) { }
    try { ed.dispatchEvent(new w.Event('blur')); ed.dispatchEvent(new w.Event('focusout', { bubbles: true })); } catch (e) { }
    await waitIdle(200, 6000);   // FBO tra mã hàng → tên, đvt, giá… xong mới sang ô kế
    return true;
  }
  window.__bcodeFillFields = async function (json) {
    var list = JSON.parse(json), done = 0;
    function fire(el, type) { try { el.dispatchEvent(new Event(type, { bubbles: true })); } catch (e) { } }
    for (var k = 0; k < list.length; k++) {
      var el = null, ew = null;
      frames(window, function (w) { if (!el) try { el = w.document.querySelector('[data-bcode-fill="' + list[k].tag + '"]'); if (el) ew = w; } catch (e) { } });
      if (!el) continue;
      if (list[k].grid) { if (await fillGridCell(ew, el, list[k].value, list[k].row || 0)) done++; continue; }
      if (el.disabled || el.readOnly) continue;
      try { el.focus(); } catch (e) { }
      el.value = list[k].value;
      fire(el, 'input'); fire(el, 'change');
      try { el.blur(); } catch (e) { }
      fire(el, 'blur'); fire(el, 'focusout');
      done++;
      await waitIdle(150, 5000);   // ô tra cứu (ma_kh → tên khách, hạn TT…) chạy xong mới điền ô kế
    }
    return done;
  };

  // Đơn vị (ma_dvcs) của phiên đăng nhập FBO — dòng đầu trang "Người sử dụng: FAST02 | 00 | Trang chính": @@unit trong điều kiện tra cứu là đơn vị này.
  window.__bcodeSessionUnit = function () {
    try {
      var t = (window.top.document.body.innerText || '').replace(/\s+/g, ' ');
      var m = t.match(/(?:Người sử dụng|User)\s*:\s*[^|]+\|\s*([^|\s]+)\s*\|/i);
      return m ? m[1] : '';
    } catch (e) { return ''; }
  };

  // ---- Mẫu điền ("🎲 Điền mẫu ▾ → Lưu form hiện tại thành mẫu", tự giữ khi bấm Lưu): đọc giá trị master + mọi dòng chi tiết đang thấy ----
  window.__bcodeReadForm = function () {
    var master = {}, rows = [], fieldsSeen = {}, diag = [];
    frames(window, function (w) {
      var all;
      try { all = w.document.querySelectorAll('input,select,textarea'); } catch (e) { return; }
      for (var i = 0; i < all.length; i++) {
        var el = all[i], m = el.id && el.id.match(FORMFIELD);
        if (!m) continue;
        var t = (el.type || '').toLowerCase();
        if (t === 'hidden' || t === 'button' || t === 'submit' || t === 'image' || t === 'file') continue;
        var r = el.getBoundingClientRect();
        if (r.width < 2 || r.height < 2) continue;
        var v = t === 'checkbox' ? (el.checked ? '1' : '0') : (el.value || '');
        if (!(m[1] in master) || (master[m[1]] === '' && v !== '')) master[m[1]] = v;
      }
      var heads;
      try { heads = w.document.querySelectorAll('[id*="HeaderText_"]'); } catch (e) { return; }
      var gvw = w.innerWidth, gvh = w.innerHeight, cols = [];
      for (var j = 0; j < heads.length; j++) {
        var hd = heads[j], hm = hd.id.match(/HeaderText_(.+)$/);
        if (!hm || fieldsSeen[hm[1]]) continue;
        var hr = hd.getBoundingClientRect();
        if (hr.width < 4 || hr.height < 4 || hr.right < 0 || hr.left > gvw || hr.top < 0 || hr.bottom > gvh) continue;
        var hit = w.document.elementFromPoint(hr.left + hr.width / 2, hr.top + hr.height / 2);
        if (!hit || !(hit === hd || hd.contains(hit) || hit.contains(hd))) continue;
        fieldsSeen[hm[1]] = 1;
        cols.push({ f: hm[1], hd: hd, x: hr.left + hr.width / 2 });
      }
      if (!cols.length) return;
      // Giá trị 1 ô lưới: ô nhập (input/select) bên trong → value; không thì chữ hiển thị.
      function cellValue(el) {
        if (!el) return '';
        if (/^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName)) return (el.type === 'checkbox' ? (el.checked ? '1' : '0') : (el.value || '')).trim();
        var inp = el.querySelector && el.querySelector('input:not([type=hidden]),textarea,select');
        var tx = (el.innerText || el.textContent || '').trim();
        if (tx === '' && inp) return (inp.type === 'checkbox' ? (inp.checked ? '1' : '0') : (inp.value || '')).trim();
        return tx;
      }
      // Cách 1 — theo bảng: vị trí cột (cellIndex) của ô tiêu đề → ô cùng vị trí ở mọi <tr> dữ liệu (đọc được cả dòng đang cuộn khuất).
      var first = w.document.elementFromPoint(cols[0].x, cols[0].hd.getBoundingClientRect().bottom + 10);
      var tr0 = first && !cols[0].hd.contains(first) && first.closest ? first.closest('tr') : null;
      diag.push('cột ' + cols.length + ', ô dưới tiêu đề: ' + (first ? first.tagName + (first.className ? '.' + String(first.className).split(' ')[0] : '') : 'không có') + (tr0 ? ' (trong <tr>)' : ''));
      if (tr0 && tr0.parentElement) {
        var trs = Array.prototype.filter.call(tr0.parentElement.children, function (r) { return r.tagName === 'TR'; });
        var idx = cols.map(function (c) { var th = c.hd.closest && c.hd.closest('td,th'); return th ? th.cellIndex : -1; });
        var byIndex = idx.every(function (i) { return i >= 0; });
        for (var ri = trs.indexOf(tr0); ri >= 0 && ri < trs.length && rows.length < 100; ri++) {
          var rec = {}, any = false;
          for (var c = 0; c < cols.length; c++) {
            var cell = byIndex ? trs[ri].cells[idx[c]] : null;
            if (!cell) {   // tiêu đề / thân là bảng khác cột: lấy theo toạ độ x trong dòng
              var rr = trs[ri].getBoundingClientRect();
              cell = rr.height > 2 ? w.document.elementFromPoint(cols[c].x, rr.top + rr.height / 2) : null;
              if (cell && !trs[ri].contains(cell)) cell = null;
            }
            var tx = cellValue(cell);
            rec[cols[c].f] = tx;
            if (tx !== '' && !/^0+([.,]0+)?$/.test(tx)) any = true;   // dòng trống: mọi ô rỗng / 0
          }
          if (!any) break;
          rows.push(rec);
        }
        return;
      }
      // Cách 2 — không có <tr>: theo toạ độ từng dòng.
      for (var row = 0; row < 60; row++) {
        var rec2 = {}, any2 = false;
        for (var c2 = 0; c2 < cols.length; c2++) {
          var y = rowCenterY(w, cols[c2].hd, row);
          if (y < 0 || y > gvh) continue;
          var cell2 = w.document.elementFromPoint(cols[c2].x, y);
          if (!cell2 || cols[c2].hd.contains(cell2)) continue;
          var tx2 = cellValue(cell2);
          rec2[cols[c2].f] = tx2;
          if (tx2 !== '' && !/^0+([.,]0+)?$/.test(tx2)) any2 = true;
        }
        if (!any2) break;
        rows.push(rec2);
      }
    });
    return JSON.stringify({ master: master, rows: rows, diag: diag.join(' | ') });
  };
  // Bấm "Lưu" trên form FBO → gửi giá trị form (đọc TRƯỚC khi FBO xử lý) về Bcode để giữ làm mẫu "Lần lưu gần nhất".
  document.addEventListener('click', function (e) {
    try {
      var el = e.target && e.target.closest && e.target.closest('a,button,input[type=button],input[type=submit],span,div');
      if (!el) return;
      var tx = (el.value || el.textContent || '').trim();
      if (!/^L(ư|u)u$/i.test(tx)) return;
      var top = window.top;
      if (!top.__bcodeReadForm) return;
      var data = top.__bcodeReadForm();
      (top.chrome || window.chrome).webview.postMessage({ type: 'bcode-fbo-saved', url: String(top.location.href), data: data });
    } catch (err) { }
  }, true);
})();
""";
}
