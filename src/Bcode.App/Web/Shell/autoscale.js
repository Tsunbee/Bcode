// Tự co giãn trang theo cỡ cửa sổ cho các trang đặt kích thước bằng px (hộp thoại / form): phóng nhẹ cả trang theo chiều rộng cửa sổ — cùng công thức với
// các trang dùng rem (html { font-size: clamp(12px, 0.4vw + 7.5px, 16px) } trên nền 13px). Dùng CSS zoom của Chromium: bố cục vẫn lấp đầy cửa sổ (đã kiểm tra:
// 100vh / thanh dưới / cuộn không lệch), nên không cần sửa từng đơn vị px trong trang. Gắn bằng <script src="autoscale.js"></script> ở <head>.
(function () {
  var root = document.documentElement;
  function apply() {
    var w = window.innerWidth || 1000;
    var z = Math.min(16 / 13, Math.max(12 / 13, (0.004 * w + 7.5) / 13));
    root.style.zoom = z.toFixed(3);
  }
  apply();
  window.addEventListener('resize', apply);
})();
