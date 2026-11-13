// Thú cưng trên thanh menu của BcodeViewer — CHÍNH engine statuspets.js của Bcode (link lúc build, xem BcodeViewer.App.csproj).
// Cài đặt + môi trường lấy từ host (Host/ViewerPets.cs: mục "StatusPets" trong settings.json của Bcode); hỏi lại mỗi 10 giây và khi cửa sổ
// được chọn lại — đổi ở Bcode (Settings → Thú cưng...) là Viewer theo. Không có phản ứng chạy SQL / nhắc nghỉ (đó là việc của Bcode).
(function () {
  var pets = null, bar = null, lastSettings = '', lastEnv = null, timer = 0;

  function call() {
    return window.bcodeHost && window.bcodeHost.call ? window.bcodeHost.call('BeginGetStatusPets') : Promise.reject(new Error('no host'));
  }

  function pushEnv() {
    if (!pets || !lastEnv) return;
    pets.env(Object.assign({}, lastEnv, { active: document.hasFocus() }));
  }

  function refresh() {
    if (!pets) return;
    call().then(function (raw) {
      var r = typeof raw === 'string' ? JSON.parse(raw) : raw;
      if (!r || !r.settings) return;
      var s = JSON.stringify(r.settings);
      if (s !== lastSettings) { lastSettings = s; pets.set(r.settings); }
      lastEnv = r.env || {};
      pushEnv();
    }).catch(function () { /* host chưa sẵn sàng — lần sau hỏi lại */ });
  }

  function start() {
    if (pets) return;
    bar = document.querySelector('#shellTop .shMenubar');
    if (!bar || !window.StatusPets) { setTimeout(start, 1000); return; }
    bar.style.position = 'relative';   // canvas của thú cưng phủ đúng thanh menu
    // Bấm trúng tiêu đề menu / nút thì là bấm menu, không phải bấm con thú.
    pets = window.StatusPets.create(bar, null, { ignore: '.shMenuTitle,.shMenuRight,button,a,input,select' });
    refresh();
    timer = setInterval(refresh, 10000);
    window.addEventListener('focus', refresh);
    window.addEventListener('blur', pushEnv);
  }

  if (document.readyState === 'complete') setTimeout(start, 300);
  else window.addEventListener('load', function () { setTimeout(start, 300); });
})();
