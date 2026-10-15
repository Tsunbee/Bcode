// Đo độ mượt: mỗi khi luồng trang bị chặn >= THRESHOLD_MS (PerformanceObserver 'longtask'), ghi 1 dòng vào %AppData%\Bcode\viewer-perf.log
// (qua host.NotifyPerfLog) kèm VIỆC VỪA LÀM trước đó (đổi tab, gõ phím, xem trước Dir, kiểm lỗi, outline, gợi ý code...), file đang mở và thời lượng.
// Khi thấy giật chỉ cần gửi file log (Help → "Log độ mượt") thay vì tả lại thao tác. Chi phí: vài biến + 1 listener, ghi đĩa ở host, gom 2 giây 1 lần.
//
// Không sửa logic các file khác: các việc được gắn nhãn bằng cách bọc hàm sau khi trang đã nạp xong (BcodePerf.wrap).
(function () {
  const THRESHOLD_MS = 100;
  const recent = [];          // { t, label } — việc vừa bắt đầu, giữ ~40 mục gần nhất
  const pending = [];         // dòng log chờ gửi host
  let flushTimer = 0;

  const fileName = () => {
    const p = window.bcodeViewer && window.bcodeViewer.activePath;
    return p ? p.split('\\').pop() : '-';
  };

  function mark(label) {
    recent.push({ t: performance.now(), label });
    if (recent.length > 40) recent.shift();
  }

  /// Bọc obj[name] để mỗi lần gọi ghi nhãn (đồng bộ + phần chạy sau await vẫn được tính nhờ cửa sổ thời gian khi gán long task).
  function wrap(obj, name, label) {
    if (!obj || typeof obj[name] !== 'function' || obj[name].__perfWrapped) return;
    const orig = obj[name];
    const w = function (...args) {
      mark(typeof label === 'function' ? label.apply(this, args) : label);
      return orig.apply(this, args);
    };
    w.__perfWrapped = true;
    obj[name] = w;
  }

  function flush() {
    flushTimer = 0;
    if (!pending.length) return;
    const text = pending.splice(0).join('\n');
    try { window.bcodeNotify('NotifyPerfLog', text); } catch { /* host cũ chưa có — bỏ qua */ }
  }

  function record(start, duration) {
    // Việc bắt đầu trong khoảng 3 giây trước cho tới hết long task: mới nhất trước, bỏ trùng.
    const from = start - 3000, to = start + duration;
    const labels = [];
    for (let i = recent.length - 1; i >= 0 && labels.length < 4; i--) {
      const r = recent[i];
      if (r.t < from) break;
      if (r.t <= to && !labels.includes(r.label)) labels.push(r.label);
    }
    const b = window.bcodeViewer;
    const flags = [
      window.bcodeDirPreview && window.bcodeDirPreview.open ? 'xemtruocDir' : '',
      window.bcodeLiteActive ? 'chedonhe' : '',
      b && b.docs ? b.docs.size + 'tab' : '',
    ].filter(Boolean).join(',');
    const now = new Date();
    const ts = now.toLocaleDateString('vi-VN') + ' ' + now.toLocaleTimeString('vi-VN');
    pending.push(`${ts}\t${Math.round(duration)} ms\t${fileName()}\t${labels.join(' ← ') || '(không rõ)'}\t${flags}`);
    if (!flushTimer) flushTimer = setTimeout(flush, 2000);
  }

  try {
    new PerformanceObserver((list) => {
      for (const e of list.getEntries()) if (e.duration >= THRESHOLD_MS) record(e.startTime, e.duration);
    }).observe({ entryTypes: ['longtask'] });
  } catch { /* trình duyệt không hỗ trợ longtask — không đo */ }

  // Phím: chỉ ghi tổ hợp có Ctrl/Alt hoặc phím đặc biệt; gõ chữ thường gom thành 1 nhãn "Gõ phím" (không ghi nội dung gõ).
  document.addEventListener('keydown', (e) => {
    if (e.repeat || e.key === 'Control' || e.key === 'Shift' || e.key === 'Alt') return;
    const combo = (e.ctrlKey ? 'Ctrl+' : '') + (e.altKey ? 'Alt+' : '') + (e.shiftKey && (e.ctrlKey || e.altKey) ? 'Shift+' : '') + e.key;
    mark(e.ctrlKey || e.altKey || e.key.length > 1 ? 'Phím ' + combo : 'Gõ phím');
  }, true);
  window.addEventListener('wheel', () => mark('Cuộn chuột'), { capture: true, passive: true });

  // Gắn nhãn các việc nặng đã biết — gọi sau khi mọi script đã nạp (perfwatch.js nạp cuối cùng).
  function attach() {
    const b = window.bcodeViewer;
    if (b) {
      wrap(b, 'activateDoc', (p) => 'Đổi tab → ' + String(p || '').split('\\').pop());
      wrap(b, 'openFile', (p) => 'Mở file ' + String(p || '').split('\\').pop());
      wrap(b, 'autoFitFont', 'Tự co chữ');
      wrap(b, 'validateActive', 'Kiểm lỗi (Problems)');
      wrap(b, 'saveActive', 'Lưu file');
      wrap(b, 'shellTree', 'Vẽ lại cây file');
    }
    // Việc nền (kiểm lỗi, outline, xem trước Dir...): ghi nhãn lúc nó THẬT SỰ chạy, không phải lúc được hẹn.
    if (window.bcodeTyping && !window.bcodeTyping.request.__perfWrapped) {
      const t = window.bcodeTyping, orig = t.request;
      t.request = function (key, fn, ...rest) { return orig.call(this, key, function (...a) { mark('Việc nền: ' + key); return fn.apply(this, a); }, ...rest); };
      t.request.__perfWrapped = true;
    }
    if (window.bcodeDirPreview) wrap(window.bcodeDirPreview, 'render', 'Xem trước Dir');
    if (window.bcodeOutline) wrap(window.bcodeOutline, 'refresh', 'Outline');
    if (window.bcodeTabs) wrap(window.bcodeTabs, 'render', 'Vẽ lại thanh tab');
    if (window.bcodeShell) wrap(window.bcodeShell, 'setTree', 'Vẽ lại cây file');
    if (window.bcodeEntity) { wrap(window.bcodeEntity, 'refreshIncludeIndex', 'Dựng chỉ mục include'); wrap(window.bcodeEntity, 'resolveActive', 'Tra entity'); }
    if (window.bcodeCompletion) {
      for (const k of Object.getOwnPropertyNames(Object.getPrototypeOf(window.bcodeCompletion))) {
        if (/^provide/i.test(k)) wrap(window.bcodeCompletion, k, 'Gợi ý code (' + k + ')');
      }
    }
  }
  // Monaco nạp bất đồng bộ (AMD) nên bcodeViewer, xem trước Dir... có thể ra đời sau 'load' — gắn lại định kỳ (attach bỏ qua hàm đã bọc).
  const tryAttach = () => { try { attach(); } catch { /* bỏ qua */ } };
  if (document.readyState === 'complete') setTimeout(tryAttach, 0); else window.addEventListener('load', () => setTimeout(tryAttach, 0));
  setInterval(tryAttach, 5000);

  window.BcodePerf = { mark, wrap };
})();
