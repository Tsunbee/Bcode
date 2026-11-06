# bcode-mark — kiểm tra thuỷ ấn truy nguồn

Dùng để **chứng minh** một bản Bcode/BcodeViewer nghi là clone có xuất phát từ bản của bạn hay không. Đây **không phải DRM**: nó không ngăn ai chụp màn hình rồi nhờ AI dựng lại — việc đó không thuỷ ấn nào chặn được. Nó chỉ giúp bạn có bằng chứng khi gặp một bản **sao chép nhị phân hoặc sao chép source/giao diện**.

## Hai lớp dấu (xem `src/Bcode.App/Services/WatermarkService.cs`)

1. **Dấu tĩnh** — GUID bộ Bcode `b3c0de00-0000-4bee-b3c0-deb3c0deb3c0`, nhúng cố định vào metadata của `Bcode.exe` / `BcodeViewer.exe`. Có mặt kể cả khi không chạy. Người lấy được source có thể gỡ.
2. **Dấu bí mật** `BCWM1:<12 hex>` = HMAC-SHA256(key gốc bản quyền, nhãn cố định), rút gọn. Ổn định theo key, một chiều nên **công bố/so khớp được mà không lộ key**. Nhúng lúc chạy vào DOM mọi trang và vào dòng đầu `error.log`. Bản build **không có** `Secrets/LocalSecrets.cs` (key rỗng) thì không mang dấu này — đúng ý: chỉ bản phát hành chính thức mới có.

## Xem dấu của CHÍNH BẠN

Mở Bcode bạn phát hành, xem dòng đầu `%AppData%\Bcode\error.log`:
```
... [info]  BcodeMark [Bcode© Bee b3c0de00-...] BCWM1:xxxxxxxxxxxx
```
`BCWM1:xxxxxxxxxxxx` là dấu của bạn.

## Kiểm tra một bản nghi ngờ

```bash
node tools/bcode-mark/check.mjs <thư-mục-hoặc-file> --mine BCWM1:xxxxxxxxxxxx
```
- Quét mọi file (exe/dll/html/js/log...), báo có GUID tĩnh không, và liệt kê mọi dấu `BCWM1:` tìm thấy.
- `--mine` để đối chiếu: trùng = đúng bản của bạn. Không cần key gốc (chỉ trích + so khớp).
- Bản nghi ngờ nếu chỉ là ảnh chụp / mô tả thì sẽ **không** mang dấu — đó là giới hạn đã biết.

## Giới hạn (nói thẳng)

- Không chặn clone bằng cách dựng lại từ ảnh/mô tả.
- Dấu bí mật chỉ xuất hiện ở **trang đang chạy** và **log** — một bản clone copy nguyên source/giao diện sẽ mang theo; một bản viết lại thì không.
- Muốn kín hơn ở lớp mã: dùng thêm obfuscator khi build Release (cân nhắc vì làm hỏng stack trace của F1 gửi lỗi).
