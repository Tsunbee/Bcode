# Bcode

Ứng dụng desktop Windows (C# / .NET 8 WinForms) lấy cảm hứng chức năng từ FCode,
viết lại từ đầu — **không sao chép hay dịch ngược bất kỳ phần nào của FCode.exe /
các .dll đi kèm** (fcontrol.dll, feditor.dll, fsystem.dll, FastBusiness.Crypto.dll...).
Toàn bộ code trong repo này do Claude viết mới dựa trên mô tả chức năng bằng lời
và ảnh chụp màn hình bạn cung cấp.

## Cập nhật gần đây

- **Fix: "SQL Query" và "Command" bị đảo ngược nhau** — bấm "SQL Query" lại ra thanh
  SELECT/FROM của builder, bấm "Command" lại ra toolbar/script tự do — ngược với đúng mô tả ban
  đầu của bạn (tính năng Open/Save/Execute/Write Schema/Check Fields/Comment/Uncomment/Options/
  Default Type/Suggest Param/Caret/Reset Connection/Result Tab là của **SQL Query**, còn
  **Command** là builder SELECT/FROM/WHERE/ORDER BY có sẵn từ trước). Đã đổi lại đúng:
  `MainForm.OpenFreeScriptTab()` (RawSqlControl) ↔ "SQL Query" (`Ctrl+Shift+Q`),
  `MainForm.OpenSelectBuilderTab()` (SqlQueryControl) ↔ "Command" (`Ctrl+Shift+C`).
- **KHÔNG triển khai**: mẹo đăng ký registry `HKCU\SOFTWARE\FCoder\ConnectStr` (chuỗi mã hoá) +
  `Ctrl+F5` để tự lấy thông tin dự án của FCode. Chuỗi đó là dữ liệu đã MÃ HOÁ bởi thuật toán
  riêng (`FastBusiness.Crypto.dll`) và việc dùng nó chắc chắn gọi tới 1 dịch vụ tra cứu dự án
  từ xa do bên phát triển FCode vận hành — đúng loại việc "dịch ngược mã hoá + gọi hạ tầng riêng
  của hãng" mà Bcode đã từ chối làm từ đầu (xem mục "Về Decrypt SQL Object" bên dưới), nên mình
  không dựng lại phần này.
  **Thay vào đó**: đã thêm `Ctrl+F5` = "Mã dự án" — dùng lại field `Workspace.ProjectId` (đã có
  sẵn, sửa được trong Choose Server/Workspaces): gõ mã dự án, Bcode tự chọn đúng Workspace đã lưu
  khớp ID đó trong danh sách WS. Cùng kiểu tiện lợi "gõ mã dự án ra thông tin dự án", nhưng dựa
  100% trên các kết nối bạn tự khai báo, không cần giải mã hay gọi ra ngoài.
- **Fix: "Cannot access a disposed object (RichTextBox)"** — `ScriptEditorControl`/`RawSqlControl`/
  `LookupControl` mỗi cái có 1 `System.Windows.Forms.Timer` để debounce syntax-highlight (hoặc debounce
  tìm kiếm ở Lookup). Timer không phải là control con nên khi đóng tab (`TabPage.Dispose()`), Timer
  KHÔNG tự dừng — nếu vừa gõ xong rồi đóng tab ngay, Timer vẫn còn 1 lần Tick đang chờ trong hàng đợi và
  bắn ra sau khi RichTextBox đã bị dispose, gây crash toàn ứng dụng. Đã sửa: dừng + dispose Timer khi
  control bị Dispose, cộng thêm `SqlSyntaxHighlighter.Apply` tự kiểm tra `box.IsDisposed` trước khi làm
  gì cả.
- **Fix: "Table" đọc bảng `...$000000` ra 0 dòng** — khác với SQL Query/Command (đã có sẵn
  `PeriodTableQueryService` gộp UNION ALL mọi bảng phân kỳ khi gõ `$000000`), tool "Table" mới thêm lần
  này đang query thẳng vào bảng `$000000` (bảng mẫu gần như rỗng) nên luôn ra 0 dòng.
  `TableDataService.LoadTableAsync` giờ dùng lại đúng `PeriodTableQueryService` để gộp — xem đủ dữ liệu
  mọi kỳ. Vì kết quả gộp không phải 1 bảng vật lý, Save bị khoá (báo rõ lý do) khi bảng đang mở là dạng
  `$000000`; muốn sửa dữ liệu thì dùng Command/SQL Query nhắm đúng 1 bảng kỳ cụ thể (vd `r00$202601`).
- **Fix: Command chạy script tay có `$000000` cũng không gộp kỳ** — `RawSqlService` giờ quét mọi
  `FROM`/`JOIN ...$000000` trong từng batch của script và thay bằng subquery UNION ALL trước khi chạy,
  giống hệt hành vi SQL Query đã có.
- **Kết quả (Result grid) có menu chuột phải** theo đúng ảnh chụp bạn gửi (`Controls/ResultGridMenu.cs`,
  gắn vào lưới kết quả của Command, SQL Query, và tab mở từ "Result Tab"): Goto Column (`Ctrl+G`), Copy
  selected/All Column Name(s), Filter (lọc theo 1 cột, dùng `DataView.RowFilter`), Add Index Column Order
  (sinh `CREATE INDEX` theo đúng thứ tự cột đã chọn — hoặc toàn bộ nếu chưa chọn — copy clipboard),
  Generate Design Fields (suy đoán kiểu SQL từ dữ liệu trả về, sinh danh sách `[cột] KIỂU NULL/NOT NULL`),
  Maxlength Column Content (độ dài nội dung lớn nhất mỗi cột — tiện để đặt size VARCHAR), Compare Column
  Content (so sánh nội dung 2 cột, bôi chọn các dòng khác nhau), Set Color Cell (tô màu nền các ô đã chọn
  — chỉ để xem, không đổi dữ liệu thật).
- **Fix: dark theme làm mờ chữ trong menu/dropdown popup** — menu chuột phải (ContextMenuStrip) và
  dropdown tràn (overflow "»") hay dropdown của nút kiểu `ToolStripDropDownButton` (như "Options..." ở
  Command) KHÔNG phải control con trong cây `.Controls`, nên `ThemeManager.Apply()` trước đây không bao
  giờ chạm tới — chúng vẫn dùng theme mặc định của Windows (chữ xám mờ khó đọc trên nền tối). Thêm
  `ThemeManager.ApplyMenu()` styling mọi loại popup này (đệ quy cả submenu), gọi tự động bất cứ khi nào
  gặp 1 ToolStrip hoặc 1 control có gán `ContextMenuStrip`.
- **Bộ tính năng mới theo menu quick-action của FCode** (SQL Query / Lookup / Table /
  Command / WCommand / File Lookup / File Reference / Change Owner / Gen Update / Note /
  Note (New), mỗi tool có phím tắt `Ctrl+Shift+<phím>` giống ảnh chụp bạn gửi: Q/L/T/C/W/F/R/O/U/E/4).
  Theo yêu cầu của bạn, **Command/WCommand/File Lookup/File Reference/Change Owner không có
  menu chuột phải** — chỉ truy cập qua toolbar hoặc phím tắt.
  - **SQL Query** (`Ctrl+Shift+Q`): tool builder SELECT/FROM/WHERE/ORDER BY cũ (`SqlQueryControl`),
    đổi tên tab từ "Command" (nhầm trước đây) thành đúng "SQL Query".
  - **Command** (`Ctrl+Shift+C`, `Controls/RawSqlControl.cs` + `Services/RawSqlService.cs`):
    tool chạy script SQL tự do (nhiều câu lệnh, tách batch bằng dòng `GO`, giống 1 cửa sổ SSMS).
    Toolbar: Open/Save file, Execute (F5), Write Schema (chèn CREATE TABLE của 1 bảng vào
    script dạng comment — đoán tên bảng từ FROM/JOIN/UPDATE/INTO đầu tiên, có thể sửa tay),
    Check Fields (dùng `SET NOEXEC ON/OFF` để parse/bind script mà không chạy thật — báo lỗi
    sai tên bảng/cột giống Execute nhưng an toàn), Comment/Uncomment (toggle `--` theo dòng),
    Options... (Word Wrap, tăng/giảm cỡ chữ), Default Type (dropdown UPPER Keyword/lower
    Keyword — viết hoa/thường toàn bộ từ khoá SQL trong script ngay khi chọn), Suggest
    Param/Caret (bật autocomplete: `Ctrl+Space` hiện gợi ý gồm từ khoá SQL + tên bảng/view +
    tên cột của các bảng đang được FROM/JOIN/UPDATE/INTO trong script), Reset Connection
    (bật = mỗi lần Execute dùng connection mới nên `#temp table` tự dọn, không cần DROP hay mở
    query mới — tắt = giữ nguyên 1 connection giữa các lần Execute, `#temp table`/`SET` tồn tại
    xuyên suốt giống 1 cửa sổ query SSMS đang mở), Result Tab (bật = kết quả mở ra 1 tab mới
    thay vì hiện trong lưới ngay dưới script).
    ⚠️ Hành vi chính xác của Write Schema/Default Type/Suggest Param/Caret trong FCode gốc
    không thấy rõ hết qua ảnh chụp — đây là suy luận hợp lý nhất từ tên nút, chỉnh lại nếu
    không đúng ý bạn.
  - **Lookup** (`Ctrl+Shift+L`, `Controls/LookupControl.cs`): viết lại hoàn toàn theo ảnh chụp
    thứ 2 bạn gửi — KHÔNG còn là ô tìm-theo-1-cột đơn giản nữa (`LookupForm`/`LookupService`
    cũ vẫn còn trong repo nhưng không dùng tới). Giờ là danh sách object có thể lọc theo loại
    (Function/Store/Table/View/Trigger checkbox) + Search Text (tìm theo tên), chọn 1 object
    xem definition (dùng lại `SqlSyntaxHighlighter`), 3 checkbox Show Search Text/Show Data/
    Show History hoạt động như radio chọn panel phụ bên dưới: Show Search Text = liệt kê các
    dòng trong definition có chứa từ đang tìm (double-click để nhảy tới dòng đó); Show Data =
    xem trước Top 100 dòng dữ liệu (chỉ áp dụng cho Table); Show History = danh sách object đã
    xem gần đây trong phiên làm việc này (tối đa 50, không lưu ra đĩa). Nút "Mở trong Tab" mở
    definition object đang chọn ra 1 tab đầy đủ (dùng lại logic dedup tab của SQL Object).
  - **Table** (`Ctrl+Shift+T`, `Controls/TableEditControl.cs` + `Services/TableDataService.cs`):
    mở 1 bảng ra lưới chỉnh sửa trực tiếp kiểu Excel — thêm/sửa/xoá dòng rồi Save sẽ tự sinh
    đúng INSERT/UPDATE/DELETE (khoá theo Primary Key thật của bảng, đọc từ `sys.indexes`).
    Bảng không có PK sẽ báo và từ chối Save (gợi ý dùng Command để tự viết).
  - **File Reference** (`Ctrl+Shift+R`, `Controls/FileReferenceControl.cs` +
    `Services/FileReferenceService.cs`): grep nội dung mọi file nguồn dưới `App_Data` tìm 1 từ
    khoá/tên link — khác File Lookup (tìm theo TÊN file), cái này tìm "ai đang tham chiếu tới
    cái này" trước khi đổi tên/sửa 1 file dùng chung.
  - **Change Owner** (`Ctrl+Shift+O`, `Forms/ChangeOwnerForm.cs` + `Services/ChangeOwnerService.cs`):
    chuyển 1 object sang schema khác bằng `ALTER SCHEMA ... TRANSFER` (tương đương
    `sp_changeobjectowner` đời cũ — SQL Server hiện đại dùng schema làm ranh giới "owner").
  - **Gen Update** (`Ctrl+Shift+U`): đã có sẵn trong menu chuột phải lưới kết quả của SQL Query
    (`SqlQueryControl.GenUpdateSelected`, sinh UPDATE cho các dòng ĐÃ CHỌN); giờ thêm phím tắt
    toàn cục sinh UPDATE cho TOÀN BỘ kết quả truy vấn gần nhất (`MainForm.GenUpdateFromLastResult`,
    dùng chung `Services/GenUpdateService.cs`) — tiện khi không mở sẵn tab SQL Query.
  - **Note / Note (New)** (`Ctrl+Shift+E` / `Ctrl+Shift+4`, `Controls/NoteControl.cs` +
    `Services/NoteService.cs`): ghi chú nhanh dạng text lưu theo từng workspace tại
    `%AppData%\Bcode\Notes\<workspace>\<tên note>.txt`. "Note" mở/tạo note tên "default";
    "Note (New)" luôn tạo tên chưa dùng ("Note 1", "Note 2"...).
  - **SQL Object nhận diện thêm Trigger**: `sys.objects` filter thêm loại `'TR'`,
    `SqlObjectKind` có thêm `Trigger` (`Models/SqlObjectInfo.cs`, `Services/SqlObjectBrowserService.cs`).
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
- **Nút ✕ đóng tab (điều thực sự bạn hỏi ở "cho tắt nội dung tạm")** — các tab bên phải
  (SQL Object, Command, File Lookup, Script Cart...) trước đây không có cách nào đóng lại,
  cứ mở là chồng thêm mãi. Giờ mỗi tab có dấu ✕ riêng (vẽ tay qua owner-draw, không đổi
  cách các TabControl khác trong app hoạt động) — bấm vào là đóng tab đó; nếu tab đang có
  thay đổi script chưa lưu sẽ hỏi lại trước khi đóng — xem `ThemeManager.MakeClosable`,
  `MainForm.CloseDocumentTab`.
- **Tô màu cú pháp SQL trong script viewer** — ô xem script (`ScriptEditorControl`) đổi từ
  `TextBox` phẳng sang `RichTextBox` có tô màu: từ khoá (xanh dương), chuỗi (cam), comment
  (xanh lá), số, và dòng `GO` tô nền đỏ giống FCode — xem `Controls/SqlSyntaxHighlighter.cs`.
  Tô lại toàn bộ khi mở script và tô lại (debounce 400ms) khi đang gõ, không phải logic bôi
  màu theo từng ký tự lúc gõ nên không giật lag với script dài.

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

**Fix: `$000000` không lọc ra data gì (chạy như bảng rỗng)** — nguyên nhân: regex
`FromRefPattern` trong `SqlQueryService` dùng `[\w]+` để bắt tên bảng ở ô FROM, mà `\w`
KHÔNG bao gồm ký tự `$` — nên với bất kỳ input nào có dạng `...$000000` thì regex luôn
`Success=False` ngay từ đầu, khiến `ResolveFromClauseAsync` luôn rơi vào nhánh "không phải
`$000000`, giữ nguyên" và chạy thẳng vào bảng `$000000` thật (chỉ là bảng mẫu/gần như rỗng)
thay vì UNION ALL qua các bảng kỳ. Đã sửa: thêm `$` vào character class bắt tên bảng/schema
(`[\w$]+`), đã test lại với các input `m21$000000`, `dbo.m21$000000`, `[dbo].[m21$000000]`,
`m21$000000 a`, `dbo.m21$000000 AS a` — tất cả match đúng và tách đúng phần bảng/alias.

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
