// Cầu nối giữa index.html (giao diện Excel → RPT, vốn chạy với webapp/server.py) và Bcode.
//
// Trang gọi fetch("/api/...") như gọi server. Ở đây:
//   · POST /api/*  -> gửi qua chrome.webview.postMessage, C# (RptApiBridge) trả lời lại.
//     Không dựa vào WebResourceRequested cho POST vì thân request dạng File/Blob không phải
//     lúc nào WebView2 cũng chuyển sang được (e.Request.Content có thể null).
//   · GET /api/file -> WebView2 tự chặn bằng WebResourceRequested (không có thân), dùng cho
//     iframe xem trước PDF.
//   · "Tải .rpt về máy" / "Tải PDF" -> hộp thoại Save As của Windows thay cho tải trình duyệt.
(() => {
  const wv = window.chrome && window.chrome.webview;
  if (!wv) return;

  const pending = new Map();
  let seq = 0;

  function toBase64(bytes) {
    let bin = "";
    for (let i = 0; i < bytes.length; i += 0x8000)
      bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(bin);
  }

  function fromBase64(b64) {
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return bytes;
  }

  async function bodyBytes(b) {
    if (b == null) return new Uint8Array(0);
    if (b instanceof Blob) return new Uint8Array(await b.arrayBuffer());
    if (b instanceof ArrayBuffer) return new Uint8Array(b);
    if (ArrayBuffer.isView(b)) return new Uint8Array(b.buffer, b.byteOffset, b.byteLength);
    return new TextEncoder().encode(String(b));
  }

  const nativeFetch = window.fetch.bind(window);
  window.fetch = async (input, init = {}) => {
    const url = typeof input === "string" ? input : input.url;
    const method = (init.method || "GET").toUpperCase();
    if (!url.startsWith("/api/") || method === "GET") return nativeFetch(input, init);

    const body = toBase64(await bodyBytes(init.body));
    const id = ++seq;
    return new Promise(resolve => {
      pending.set(id, resolve);
      wv.postMessage({ kind: "api", id, url, method, body });
    });
  };

  wv.addEventListener("message", e => {
    const m = e.data;
    if (!m) return;
    if (m.kind === "api") {
      const resolve = pending.get(m.id);
      if (!resolve) return;
      pending.delete(m.id);
      resolve(new Response(m.body, {
        status: m.status,
        headers: { "Content-Type": "application/json; charset=utf-8" },
      }));
    } else if (m.kind === "loadXlsx") {
      // Mở sẵn một file Excel (vd chuột phải file .xlsx trong File Lookup): dựng File rồi
      // giả lập chọn file ở ô "Excel mẫu in", để đi đúng luồng nạp của trang
      const input = document.getElementById("fXlsx");
      if (!input) return;
      const file = new File([fromBase64(m.b64)], m.name,
        { type: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" });
      const dt = new DataTransfer();
      dt.items.add(file);
      input.files = dt.files;
      input.dispatchEvent(new Event("change"));
    } else if (m.kind === "log" && typeof log === "function") {
      log(m.text);
    } else if (m.kind === "outDir") {
      const el = document.getElementById("outDir");
      if (el && !el.value) el.value = m.value || "";
    }
  });

  function saveAs(path, name) {
    if (path) wv.postMessage({ kind: "save", path, name: name || "" });
  }

  document.addEventListener("DOMContentLoaded", () => {
    // downloadRpt / PDF_PATH / LAYOUT là biến toàn cục của script trang — gán lại SAU khi
    // trang đã khai báo xong
    window.downloadRpt = (srcPath, name) => saveAs(srcPath, name);
    const pdfDl = document.getElementById("pdfDl");
    if (pdfDl) pdfDl.onclick = () => {
      if (typeof PDF_PATH !== "undefined" && PDF_PATH)
        saveAs(PDF_PATH, ((typeof LAYOUT !== "undefined" && LAYOUT && LAYOUT.report_name) || "preview") + ".pdf");
    };

    const outDir = document.getElementById("outDir");
    if (outDir) {
      outDir.placeholder = "để trống = thư mục Rpt của workspace";
      outDir.title = "Thư mục lưu file .rpt. Có thể dán đường dẫn mạng, ví dụ "
        + "\\\\172.168.5.14\\...\\Templates\\Rpt. Lưu không được thì dùng nút 'Tải .rpt về máy'.";
    }
    const dl = document.querySelector("#pdfDl");
    if (dl) dl.title = "Lưu file PDF xem trước";

    wv.postMessage({ kind: "ready" });
  });
})();
