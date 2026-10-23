# Secrets — bí mật của Bcode (không nằm trong git)

- `LocalSecrets.cs` — **file bí mật thật**: tài khoản DAC mặc định của Decrypt SQL Object, thông tin kết nối FSG_A (đồng bộ project / yêu cầu FSG / chuông thông báo), mật khẩu mặc định của server nội bộ và login Profiler, khoá RSA mẫu của Setup eInvoice. File nằm trong `.gitignore` — **không commit, không gửi qua chat / mail thường**.
- `LocalSecrets.Empty.cs` — bản rỗng cùng tên hằng, được commit. `Bcode.App.csproj` chỉ build bản này khi thư mục không có `LocalSecrets.cs`.

## Máy mới / máy khác pull về

1. Lấy `LocalSecrets.cs` từ nơi lưu an toàn (người giữ bí mật), chép vào `src\Bcode.App\Secrets\`.
2. Build lại Bcode.App.

Không có file thì vẫn build và chạy được bình thường. Chỉ các tính năng trên báo "build thiếu file bí mật Secrets\LocalSecrets.cs", và bản phát hành build từ máy đó cũng sẽ thiếu các tính năng này.

## Thêm / đổi bí mật

- Thêm hằng vào **cả hai** file (`LocalSecrets.cs` có giá trị thật, `LocalSecrets.Empty.cs` để `""`). Code đọc qua `LocalSecrets.<Tên>` và phải tự xử lý trường hợp chuỗi rỗng.
- Đổi mật khẩu (vd sau khi lộ): sửa `LocalSecrets.cs` rồi gửi bản mới cho các máy build.
- Thông tin FSG_A trong file vẫn ở dạng mã hoá AES như trước (`FsgEnc*` + `FsgKeyPassphrase`). Muốn tạo blob mới thì dùng lại định dạng ở `FsgProjectLookupService.Decrypt`: base64(nonce 12B | tag 16B | cipher), khoá = SHA-256(passphrase).

Lưu ý: hằng vẫn được biên dịch vào Bcode.exe. File này chỉ đưa bí mật ra khỏi source/git, **không** bảo vệ được trước người có file exe.
