# Assets/

- `app.ico` — icon BcodeViewer, nhiều cỡ (16, 20, 24, 32, 40, 48, 64, 128, 256). Dùng làm
  `<ApplicationIcon>` (icon của file .exe) và được chép cạnh exe để `MainForm` nạp làm icon
  cửa sổ / taskbar lúc chạy.
- `app_master.png` — bản gốc 1024x1024 mà `app.ico` được thu nhỏ từ đó.

Icon được sinh bằng code, đồng bộ với icon Bcode (`src/Bcode.App/Assets/logo/logo_B_mirror.png`).
Nét chữ B lấy từ logo Bcode rồi lật lại cho đúng chiều. Chữ B màu đen, đặt trên nền xanh,
có ô `</>` nhỏ ở góc dưới bên phải giống bố cục của Bcode. Ở cỡ 16–24 ô này được vẽ to hơn,
chỉ còn `< >` nét dày để vẫn nhận ra được. Không dùng font hay ảnh của bên thứ ba.
