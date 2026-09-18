# Bcode

Ứng dụng desktop Windows (C# / .NET 8 WinForms) lấy cảm hứng chức năng từ FCode,
viết lại từ đầu — **không sao chép hay dịch ngược bất kỳ phần nào của FCode.exe /
các .dll đi kèm** (fcontrol.dll, feditor.dll, fsystem.dll, FastBusiness.Crypto.dll...).
Toàn bộ code trong repo này do Claude viết mới dựa trên mô tả chức năng bằng lời
và ảnh chụp màn hình bạn cung cấp.

## Cập nhật gần đây

- **Giao diện**: bỏ giao diện WinForms xám mặc định, tự viết theme flat/dark
  (và flat/light) không phụ thuộc thư viện ngoài — xem `UI/ColorPalette.cs`,
  `UI/ThemeManager.cs`, `UI/FlatToolStripRenderer.cs`, `UI/ThemedForm.cs`.
  Nút "Dark Theme" ở góc phải menu bar (giống FCode) chuyển đổi 2 theme ngay
  lập tức. Mọi Form đều kế thừa `ThemedForm` để tự động được áp theme.
- **Workspace (Edit Project)**: cập nhật đúng theo cấu trúc thật của FastBusiness
  — tách riêng **Sys Data** (bảng `wcommand`, user, menu...) và **App Data**
  (bảng nghiệp vụ `m21$...`, `c21$...`...), cộng thêm Program Path, Source Path,
  Mobile Path, Working Path, Registry Name, ID, Login WLink — xem `Models/Workspace.cs`
  và `Forms/ConnectionSettingsForm.cs`. `DbConnectionService.CreateConnection(useSysDatabase:)`
  chọn đúng database cho từng thao tác (WCommand luôn dùng Sys Data).
- **Quick Access**: nút "☰ Quick Access" đầu Tools toolbar mở hộp thoại chọn ẩn/hiện
  từng tool, tránh toolbar dài vô hạn khi thêm tool mới — xem `Forms/QuickAccessForm.cs`,
  `AppSettings.HiddenToolKeys`, `MainForm.RebuildToolsBar()`.
- **SQL Object đổi qua lại Sys Data / App Data**: thêm combobox chọn database ngay trong
  tab SQL Object (`Controls/SqlObjectTreeControl.cs`), thay vì cố định 1 database —
  đúng hành vi "chuyển qua lại các database để lọc thông tin" của FCode.
- **WCommand → File Lookup**: click 1 menu trong cây WCommand giờ mở (hoặc dùng lại)
  tab File Lookup, tự tìm kiếm mọi file nguồn khớp tên link đó (không giới hạn 1 phần mở
  rộng), thay vì đoán và mở đại 1 file — xem `MainForm.OpenWCommandItem`,
  `FileLookupControl.SearchFor`.
- **Fix: File Lookup hiện cả cây App_Data không lọc gì cả khi search từ WCommand** —
  lỗi ở `FileLookupService.PopulateRecursive`: khi bật "search mọi phần mở rộng"
  (`onlyShowFiltered = false`), điều kiện cắt tỉa thư mục rỗng lại luôn đúng (`!onlyShowFiltered`
  luôn `true`) nên KHÔNG thư mục nào bị ẩn dù không khớp gì — hiện nguyên cây. Đã sửa: tách
  riêng "có bật lọc phần mở rộng" và "có nên cắt bớt thư mục rỗng" thành 2 điều kiện độc lập
  (`pruneEmptyFolders = onlyShowFiltered || có searchText`), giờ search sẽ chỉ hiện đúng
  nhánh có file khớp.
- **Fix: sai gốc duyệt File Lookup** — trước đó gốc cứng là `App_Data\Controllers`
  (suy đoán từ cấu trúc template cài đặt của FCode, không phải cấu trúc site thật). Theo
  ảnh chụp site thật bạn gửi, gốc đúng là `App_Data` (con trực tiếp: Include/Request/
  Structure/Templates) — đã sửa `MainForm.OpenFileLookupTab`.
- **Fix: Edit Project (Workspaces) UI vỡ layout + Test Connection/Save "không có gì xảy ra"** —
  nguyên nhân gốc: các dòng checkbox "Integrated Security" và dòng Test Connection được
  chèn vào `TableLayoutPanel` bằng cách đọc `form.RowCount` 2 lần liên tiếp mà không hề
  tăng nó lên, nên những control này bị **chồng đè lên đúng dòng trước đó** (checkbox đè
  lên Password, dòng Test Connection đè lên dòng cuối Project) — đó là lý do phần header
  "Database" nhìn "trôi" cách xa các field của nó, và vì sao bấm Test Connection/Save
  "im ru" (nút bị control khác đè lên, không bắt được click). Đã viết lại toàn bộ
  `Forms/ConnectionSettingsForm.cs`:
  - Mỗi dòng field giờ dùng 1 helper tự quản lý chỉ số dòng nhất quán (snapshot + tăng
    trong cùng 1 lệnh), không còn đọc lại `RowCount` một cách mập mờ.
  - Chia lại giao diện thành 3 khối `GroupBox` rõ ràng: **Kết nối** (Tên WS/Server/
    Integrated Security/User/Password), **Database** (Sys Data/App Data), **Project**
    (ID/Login WLink/Program Path/Source Path/Mobile Path/Working Path/Registry Name),
    xếp dọc trong 1 panel cuộn được.
  - Thanh **Test Connection** và **Save & Close / Apply** giờ neo cố định ở đáy hộp
    thoại (`Dock.Bottom`), luôn nhìn thấy và bấm được, tách hẳn khỏi vùng cuộn phía trên.
- **Fix: click lại 1 table/view/proc mở tab trùng, tab cũ giữ nội dung cũ** — `SQL Object`
  giờ nhớ tab đang mở cho từng object (theo tên đủ + database Sys/App), click lại chỉ
  nạp lại đúng tab đó thay vì mở thêm 1 tab "dbo.hddtr00" mới đè lên tab cũ — xem
  `MainForm._objectTabs`, `OpenObjectDefinitionAsync`.
- **Bổ sung Index + Trigger vào script CREATE TABLE tự sinh** — trước đây chỉ có cột;
  giờ `SqlObjectBrowserService.GenerateCreateTableAsync` nối thêm khối `-- Indexes`
  (đọc `sys.indexes`/`sys.index_columns`, tự phát hiện Primary Key/Unique) và khối
  `-- Triggers` (đọc `sys.triggers` + `OBJECT_DEFINITION`) ngay sau `CREATE TABLE`.
- **Cho tắt dòng "(nội dung tạm)"**: các tab nội dung sinh ra (không phải file thật —
  script SQL Object, Script Cart) giờ có nút ✕ ở đầu dòng để ẩn thanh đó đi; đã ẩn thì
  nhớ luôn cho những tab mở sau (lưu `AppSettings.ShowTempContentBar`) — xem
  `ScriptEditorControl.ShowPathBar`/`TempBarHidden`.
- **Command: gợi ý tên table cho ô FROM** — ô FROM giờ autocomplete theo tên table/view
  (cả dạng `schema.name` và tên trần) gộp từ cả App Data lẫn Sys Data, nạp 1 lần khi mở
  tab Command — xem `SqlQueryControl.LoadFromSuggestionsAsync`.

## Mở project

1. Cài .NET 8 SDK (nếu Visual Studio 2022 17.8+ thì đã có sẵn) và workload
   ".NET Desktop Development".
2. Mở `Bcode.sln` bằng Visual Studio, hoặc chạy `dotnet run --project src/Bcode.App`
   từ terminal trên Windows.
3. Build đã được xác nhận biên dịch sạch (0 warning, 0 error) ở môi trường build này.

## Kiến trúc

```
src/Bcode.App/
  Program.cs                 Entry point
  Forms/
    MainForm.cs               Cửa sổ chính: menu, toolbar, tab trái/phải
    ConnectionSettingsForm.cs Quản lý danh sách Workspace (WS)
    CompareTextForm.cs        Compare Text (diff 2 khối text)
    CompareStructureForm.cs   Compare Structure (diff cột 2 bảng, 2 WS)
    StringBeautyForm.cs       Format SQL
    LibrarySnippetForm.cs     Thư viện snippet
    CreateRptXlsxForm.cs      Xuất .xlsx (thật) — .rpt để stub, xem ghi chú trong file
    StubForm.cs                CreateProcessing / CheckMail / DecryptSqlObject /
                                SetupEInvoice / ViewRptInFec — khung sẵn + TODO rõ ràng
  Controls/
    WCommandTreeControl.cs    Cây menu từ bảng wcommand
    SqlObjectTreeControl.cs   Cây Table/View/Procedure/Function
    FileLookupControl.cs      Duyệt Controllers/Filter|Grid|Report qua UNC
    SqlQueryControl.cs        Tool SELECT/FROM/WHERE/ORDER BY + Run
    ScriptEditorControl.cs    Ô xem/sửa nội dung script
  Services/
    DbConnectionService.cs      Quản lý kết nối theo WS đang chọn
    WCommandService.cs          Query + dựng cây từ bảng wcommand
    SqlObjectBrowserService.cs  Liệt kê & lấy định nghĩa object SQL Server
    PeriodTableQueryService.cs  *** Logic bảng phân kỳ $000000 (xem bên dưới) ***
    SqlQueryService.cs          Ghép SELECT/FROM/WHERE/ORDER BY, áp dụng PeriodTableQueryService
    GenInsertService.cs         Sinh câu INSERT từ DataTable
    DiffService.cs               Diff dòng kiểu LCS (không phụ thuộc thư viện ngoài)
    SchemaCompareService.cs      So sánh cột giữa 2 bảng
    SqlFormatterService.cs       Format SQL cơ bản (không phụ thuộc thư viện ngoài)
    FileLookupService.cs         Duyệt cây thư mục UNC theo quy ước Controllers/...
    ScriptFileService.cs         Đọc/ghi file, "script cart" cho Add/View/Clear/Save/Copy Script
    XlsxExportService.cs         Xuất .xlsx thật, dùng thư viện mã nguồn mở ClosedXML
    IDecryptionProvider.cs       Interface rỗng cho Decrypt SQL Object — xem ghi chú
  Models/                       Các model dữ liệu tương ứng (Workspace, WCommandItem, ...)
```

## Tính năng đã chạy thật (không phải stub)

- Quản lý nhiều Workspace (WS): server, database, UNC source root, test connection.
- Cây menu WCommand đọc trực tiếp từ bảng `wcommand`, double-click mở file nguồn
  tương ứng qua quy ước `Controllers/{Dir,Filter,Grid,Report,Lookup}/<link>.f`.
- Cây SQL Object (Tables/Views/Procedures/Functions) — xem định nghĩa qua
  `OBJECT_DEFINITION` (procs/views/functions) hoặc tự sinh `CREATE TABLE` (bảng).
- **Tool SQL Query (SELECT/FROM/WHERE/ORDER BY) với logic bảng phân kỳ `$000000`**
  (xem mục riêng bên dưới) — đúng yêu cầu bạn mô tả.
- File Lookup: duyệt cây thư mục theo UNC path, lọc theo phần mở rộng, tìm kiếm tên file.
- Add/View/Clear/Save/Copy Script: giỏ script nhiều file, xem gộp, lưu lại, copy clipboard.
- Gen Insert: chọn dòng trong kết quả query → sinh câu `INSERT INTO ...` → copy clipboard.
- Compare Text: diff hai khối text bất kỳ (thuật toán LCS tự viết).
- Compare Structure: diff cột giữa 2 bảng (có thể ở 2 Workspace khác nhau).
- String Beauty: format lại khối SQL theo mệnh đề (SELECT/FROM/WHERE/JOIN/...).
- Library: kho snippet đặt tên, lưu file JSON, insert thẳng vào script editor đang mở.
- Create *.xlsx: xuất bảng kết quả query ra file Excel thật (dùng ClosedXML — mã nguồn mở).
- Backup Database: chạy `BACKUP DATABASE ... TO DISK` thật qua kết nối hiện tại.

## Tính năng để khung + TODO rõ ràng (chưa triển khai đầy đủ)

Những tool sau mở ra 1 form thật, có mô tả "cần làm gì" ngay trong giao diện
(xem `Forms/StubForm.cs`), vì chúng phụ thuộc thông tin/hạ tầng riêng của bạn
(SMTP, nhà cung cấp eInvoice...) hoặc phụ thuộc định dạng đóng của bên thứ ba
(Crystal Reports .rpt) mà Bcode không sao chép:

- Create Processing (sinh bộ file từ template — cần bạn tự thiết kế template)
- Check Mail (cần cấu hình SMTP thật)
- Decrypt SQL Object (xem mục "Về Decrypt SQL Object" bên dưới)
- Setup eInvoice / FE (cần API của nhà cung cấp hoá đơn điện tử bạn dùng)
- .rpt trong Create *.rpt, *.xlsx (Crystal Reports là định dạng đóng, thương mại)
- View Rpt in FEC (gọi ứng dụng ngoài — chỉ cần biết đường dẫn app thật)
- Mobile tab (cần bạn xác nhận đúng quy ước thư mục mobile trên server)

## Về logic bảng phân kỳ `$000000` (tính năng bạn yêu cầu cụ thể)

Đúng như bạn mô tả: các bảng giao dịch kiểu `m21$000000`, `c21$000000`, `d21$000000`...
trên thực tế được chia vật lý theo kỳ, ví dụ `m21$202601`, `m21$202602`, ...
`PeriodTableQueryService` làm 2 việc:

1. `DiscoverPeriodTablesAsync`: query `sys.tables` để tìm mọi bảng khớp mẫu
   `<base>$<6 số>` trong schema hiện tại (mặc định loại trừ chính `$000000`
   vì đó chỉ là bảng mẫu/rỗng).
2. `BuildUnionSubquery`: ghép các bảng tìm được thành 1 subquery
   `(SELECT * FROM base$202601 UNION ALL SELECT * FROM base$202602 UNION ALL ...) AS base$000000`.

`SqlQueryService.ResolveFromClauseAsync` phát hiện khi ô FROM là dạng
`<base>$000000` (có hoặc không có alias) và tự thay bằng subquery gộp ở trên
trước khi chạy — đúng hành vi "gõ `$000000` là thấy hết data mọi kỳ, không phải
chọn từng bảng kỳ" mà bạn mô tả. `SqlQueryControl` hiển thị SQL cuối cùng đã
được mở rộng ở thanh trạng thái để bạn kiểm tra.

Việc suy luận này dựa trên đúng những gì bạn giải thích bằng lời (không đọc
file nào trong FCode để lấy logic này) — nếu quy ước đặt tên kỳ ở site bạn khác
đi (ví dụ theo quý, theo năm 4 số...), sửa lại regex/pattern trong
`PeriodTableQueryService` cho khớp.

## Về Decrypt SQL Object

Bcode **không** cố gắng khôi phục thuật toán mã hoá riêng của FCode/FastBusiness.Crypto.dll
— đây là tài sản thuộc bên phát triển FCode và việc dịch ngược nó nằm ngoài phạm vi
Claude có thể hỗ trợ. `IDecryptionProvider` là một interface rỗng bạn có thể cắm vào
nếu bạn tự có (hoặc được cấp phép dùng) một thuật toán giải mã hợp lệ cho object của
chính bạn.

## Những việc nên làm tiếp

- Xác nhận đúng cấu trúc cột thật của bảng `wcommand` ở DB của bạn (mình suy luận
  từ ảnh chụp: wmenu_id, wmenu_id0, menu_id, bar, bar2, link, parameter, icon_url,
  status, icon, sysid, type...); chỉnh `WCommandService`/`WCommandItem` nếu khác.
- Test `PeriodTableQueryService` với dữ liệu thật để chỉnh lại độ dài/quy ước của
  phần "6 số" sau dấu `$` nếu site bạn đặt tên khác.
- Bổ sung mã hoá mật khẩu SQL trong `AppSettings` (hiện lưu plain text trong JSON local)
  nếu máy dùng chung với người khác.
