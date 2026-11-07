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

    public static async Task AttachAsync(CoreWebView2 core)
    {
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Script);
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var t) || t.GetString() != CopyTag
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
})();
""";
}
