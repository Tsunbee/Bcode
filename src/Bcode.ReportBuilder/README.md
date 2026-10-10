# Bcode.ReportBuilder — module "Tạo báo cáo"

Tạo báo cáo kiểu Power BI cho Bcode: chọn bảng / trường / cách thể hiện (bảng thường hoặc pivot, có số dư đầu–cuối kỳ),
Bcode tự sinh procedure `zrs_*`, Filter / Grid / Report / Main `zrpt_*` và mẫu Excel.

Project riêng, nằm chung repo Bcode ở `src/Bcode.ReportBuilder` (cạnh `src/Bcode.App`). Build ra DLL riêng
(`Bcode.ReportBuilder.dll`), Bcode.App chỉ tham chiếu DLL đó — giống `ExcelToFrx.dll` / `SqlDecryptor.Core.dll` — không
tham chiếu ngược lại `Bcode.exe`.

## Build (sửa source xong)

**Sửa / gỡ lỗi hằng ngày** — build Debug như bình thường, không làm rối mã, nhanh:
```
dotnet build src/Bcode.ReportBuilder
```
Build tự chép `Bcode.ReportBuilder.dll` vào `src/Bcode.App/Libs/`. Sau đó build Bcode như bình thường (dll đi theo vào output / publish).
Repo nằm chỗ khác: `-p:BcodeAppDir=<thư mục Bcode.App>\`.

**Trước khi đưa bản cho người khác / phát hành** — build thêm một lần Release để GHI ĐÈ bản Debug bằng bản đã làm rối mã (ConfuserEx — xem
mục "Làm rối mã" dưới đây). Build Release phải dùng MSBuild.exe cổ điển (đi kèm Visual Studio), KHÔNG dùng `dotnet build -c Release` — xem lý do bên dưới:
```
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" src\Bcode.ReportBuilder\Bcode.ReportBuilder.csproj -restore -p:Configuration=Release
```
Sau đó build lại `Bcode.App` (dotnet build bình thường) để nó biên dịch và đóng gói cùng bản DLL đã làm rối mã.

## Làm rối mã (ConfuserEx)

`Bcode.ReportBuilder.crproj` khai báo các bước làm rối mã, chỉ áp dụng khi build Release (`Obfuscate` trong `.csproj` chỉ bật khi `Configuration=Release`).
Dùng gói `Confuser.MSBuild` (bản mkaring/ConfuserEx đang được duy trì), tự chạy sau khi biên dịch và thay thế luôn file DLL ở `bin\Release\...`.

- **Vì sao phải dùng MSBuild.exe cổ điển:** gói `Confuser.MSBuild` 1.6.0 có lỗi đóng gói — bản "netstandard" của task (bản mà `dotnet build`/MSBuild lõi
  .NET dùng) thiếu file `dnlib.dll` nên chạy `dotnet build -c Release` sẽ báo lỗi `Could not load file or assembly 'dnlib'`. MSBuild.exe cổ điển
  (`MSBuildRuntimeType=Full`, đi kèm Visual Studio) dùng bản "netframework" của task, có đủ `dnlib.dll`, nên build được. Build **nội dung** DLL
  (net8.0-windows) không đổi gì — chỉ công cụ chạy MSBuild là bản cũ, hoàn toàn bình thường (Visual Studio tự build .NET 8 kiểu này hằng ngày).
- **Các bước được bật:** `rename` (đổi tên lớp/hàm/biến private & internal, GIỮ nguyên tên THAM SỐ — `renameArgs="false"`, xem bên dưới),
  `ctrl flow` (làm rối luồng điều khiển), `constants` (mã hoá chuỗi/hằng số), `ref proxy` (thêm lớp trung gian khi gọi hàm),
  `anti ildasm` (chặn ILDASM mở trực tiếp).
- **Các bước CỐ Ý bỏ qua:**
  - `invalid metadata` — làm hỏng vài bảng metadata để phá công cụ dịch ngược, nhưng cũng làm Roslyn (trình biên dịch C#) không đọc được DLL này
    làm tham chiếu nữa — mà `Bcode.App` tham chiếu thẳng DLL này lúc biên dịch (qua `extern alias "rb"`), nên dùng protection này là **Bcode.App
    không build được nữa**. Đã thử và xác nhận lỗi này trước khi bỏ.
  - `anti tamper` — chèn một đoạn mã gốc (native) để tự kiểm tra bị sửa đổi; cách này chỉ chạy được trên .NET Framework (CLR cũ), **không chạy được
    trên .NET 8 / CoreCLR**. Dùng sẽ làm DLL không nạp được.
  - `anti debug`, `anti dump` — thiết kế riêng cho .NET Framework, chưa có xác nhận chạy đúng trên .NET 8 nên không bật để tránh rủi ro.
  - `resources` — mã hoá / đổi cách đóng gói tài nguyên nhúng; `Entry.cs` đọc tài nguyên bằng đúng tên (`"reportbuilder.html"`, `"reportcatalog.json"`,
    `"relations.json"`), nên không bật để khỏi ảnh hưởng.
- **`renameArgs="false"` (bắt buộc):** mặc định "rename" vẫn xoá TÊN THAM SỐ của constructor dù kiểu là public. Codebase này dùng nhiều `record`
  (`MenuRow`, `ReportMenuItem`, `GridColumnInfo`, `ProcParam`, `MetaTable`…) mà `System.Text.Json` đọc/ghi qua CONSTRUCTOR CHÍNH (khớp theo tên
  tham số), không qua set property — mất tên tham số làm `JsonSerializer` ném lỗi *"deserialization constructor ... contains parameters with
  null names"*. Đã gặp thật (mất mẫu đã lưu / lỗi khi mở "Mở báo cáo có sẵn") nên bắt buộc giữ `renameArgs="false"`.
- **Vì sao an toàn cho các lớp JSON (`ReportSpec`, `ColumnSpec`, `FilterSpec`…):** mặc định `rename` của ConfuserEx **không đổi tên các kiểu /
  thuộc tính `public`** (chỉ đổi private / internal) — nên toàn bộ các lớp đặc tả dùng để gửi/nhận JSON với trang web và với file bản nháp đã lưu
  (đều là `public`) giữ nguyên tên. Dòng rule thứ hai trong `.crproj` còn giữ hẳn namespace `Bcode.ReportBuilder` (`Entry`, `IReportHost`,
  `IWebPage`, `MenuRow`) để chắc chắn không đổi dù sau này đổi preset.
- **Đã kiểm tra (2026-10-08):** build Release xong, `Bcode.App` biên dịch lại bình thường; round-trip JSON đúng cho cả class (`ReportSpec`) lẫn
  record (`ReportMenuItem`, `MenuRow`, `GridColumnInfo`); sinh procedure có số dư + nối bảng rồi chạy thử trên KOG (chỉ đọc) ra đúng dữ liệu, ghi
  Excel OK; so sánh thống kê phân tích 202 báo cáo mẫu giữa bản Debug (chưa làm rối) và bản Release (đã làm rối) ra **cùng một kết quả hệt nhau**
  (85 sinh lại được / 18 cần chọn cột nối / 5 lỗi khác / 9 không có procedure) — xác nhận làm rối mã không đổi hành vi chương trình.
- **Chưa kiểm tra:** chưa thử bấm thật trong Bcode (chỉ thử bằng chương trình nạp DLL độc lập); chưa đo chênh lệch tốc độ build / tốc độ chạy;
  chưa thử các mức bảo vệ mạnh hơn (`maximum` preset, bật thêm `anti debug`/`anti dump` trên .NET 8 xem có thật sự lỗi không hay chỉ là thận trọng).

## Cách ghép với Bcode
- DLL **không tham chiếu Bcode.exe**. Bcode đưa các thứ DLL cần qua `IReportHost` (kết nối DB, workspace, thư mục AppData, tạo trang WebView2,
  sinh script menu) — xem `Host.cs`; phía Bcode cài đặt ở `Bcode.App\Services\ReportBuilderModule.cs`.
- Trang giao diện `Web\reportbuilder.html` và từ điển trường `Data\reportcatalog.json` + `Data\relations.json` được **nhúng vào DLL**. Khi mở tab,
  Bcode ghi trang ra `Web\Shell\reportbuilder.html` cạnh exe (để dùng chung WebView2 + theme) — chỉ ghi lại khi nội dung đổi.
- Vài file dùng chung của Bcode.App (ghi Excel, dựng rpt xml, so sánh văn bản) được **liên kết** vào project (`Shared\`) nên chỉ có một bản code;
  vì vậy Bcode.App tham chiếu DLL với alias `rb` (extern alias) để tránh trùng tên kiểu.
- `Data\relations.json` + `KnownRelations.cs`: "cây quan hệ đã học" — danh sách khoá ngoại giữa các bảng FBO đã thấy thật trong procedure từng
  phân tích, dùng để suy ra điều kiện nối khi không tìm thấy ON rõ trong văn bản procedure (thay cho đoán theo "cột đầu tiên"). Bồi thêm bằng cách
  thêm một dòng vào file này mỗi khi thấy một quan hệ chắc chắn ở báo cáo mới — chỉ thêm quan hệ đã thấy thật, không đoán.

## Lưu ý
Source đã từng nằm trong commit `79b6737` của repo Bcode (đã push lên `origin/b-custhang10`), rồi tách ra ngoài repo một thời gian
(D:\Bee\Tool\Bcode.ReportBuilder, không push) trong lúc phát triển, và gộp trở lại vào repo ở đây từ 2026-10-08.

## Cửa sổ riêng
Bcode mở "Tạo báo cáo" trong cửa sổ riêng toàn màn hình (`Bcode.App\Forms\ReportStudioForm.cs` — chế độ "① Từ procedure có sẵn" (mặc định) là `Bcode.App\Controls\QuickReportControl.cs`, chế độ "② Thiết kế từ bảng" là module này, dùng chung từ điển `ReportCatalog` và `ReportFilesDeployService.Plan` của DLL qua API public; giống BcodeViewer về trải nghiệm nhưng vẫn chạy trong tiến trình Bcode để dùng chung kết nối workspace, theme và tab SQL).
Màn hình ≥ 1280px: khối chọn bảng / trường nằm cố định bên trái; hẹp hơn thì mở dạng hộp thoại bằng nút "Chọn bảng & trường…".
