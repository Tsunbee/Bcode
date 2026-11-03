# Secrets — bí mật của Bcode (không nằm trong git)

- `LocalSecrets.cs` — **file bí mật thật**: tài khoản DAC mặc định của Decrypt SQL Object, thông tin kết nối FSG_A (đồng bộ project / yêu cầu FSG / chuông thông báo), mật khẩu mặc định của server nội bộ và login Profiler, khoá RSA mẫu của Setup eInvoice. File nằm trong `.gitignore` — **không commit, không gửi qua chat / mail thường**.
- `LocalSecrets.Empty.cs` — bản rỗng cùng tên hằng, được commit. `Bcode.App.csproj` chỉ build bản này khi thư mục không có `LocalSecrets.cs`.
- `SetupHddtSecrets.vb` + `SetupHddt.connections.config` — bí mật của **tool Setup HDDT** (`src\Setup HDDT`, VB .NET Framework): cặp khoá RSA Proxy / App (trùng `LocalSecrets.EInv*`), hash password, tài khoản portal điền sẵn để thử, và chuỗi kết nối SQL (`App.config` đọc qua `configSource`). Gitignore, không commit. `SetupHDDT.vbproj` link 2 file này vào (config được chép ra cạnh exe thành `SetupHDDT.connections.config`); không có thì link bản rỗng `SetupHddtSecrets.Empty.vb` / `SetupHddt.connections.Empty.config` (được commit) — tool vẫn build / mở được, bấm tạo script thì báo thiếu khoá. Bcode.App (C#) không biên dịch các file `.vb` / `.config` này.

## Máy mới / máy khác pull về

1. Lấy `LocalSecrets.cs` (và nếu cần build tool Setup HDDT: `SetupHddtSecrets.vb`, `SetupHddt.connections.config`) từ nơi lưu an toàn (người giữ bí mật), chép vào `src\Bcode.App\Secrets\`.
2. Build lại Bcode.App (/ SetupHDDT.vbproj).

Không có file thì vẫn build và chạy được bình thường. Chỉ các tính năng trên báo "build thiếu file bí mật Secrets\LocalSecrets.cs", và bản phát hành build từ máy đó cũng sẽ thiếu các tính năng này.

## Thêm / đổi bí mật

- Thêm hằng vào **cả hai** file (`LocalSecrets.cs` có giá trị thật, `LocalSecrets.Empty.cs` để `""`). Code đọc qua `LocalSecrets.<Tên>` và phải tự xử lý trường hợp chuỗi rỗng.
- Đổi mật khẩu (vd sau khi lộ): sửa `LocalSecrets.cs` rồi gửi bản mới cho các máy build.
- Thông tin FSG_A trong file vẫn ở dạng mã hoá AES như trước (`FsgEnc*` + `FsgKeyPassphrase`). Muốn tạo blob mới thì dùng lại định dạng ở `FsgProjectLookupService.Decrypt`: base64(nonce 12B | tag 16B | cipher), khoá = SHA-256(passphrase).

Lưu ý: hằng vẫn được biên dịch vào Bcode.exe. File này chỉ đưa bí mật ra khỏi source/git, **không** bảo vệ được trước người có file exe.
