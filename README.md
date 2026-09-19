# Bcode

Ứng dụng desktop Windows (C# / .NET 8 WinForms) lấy cảm hứng chức năng từ FCode,
viết lại từ đầu — **không decompile, không patch, không dịch ngược bất kỳ phần nào của
FCode.exe / các .dll đi kèm** (fcontrol.dll, feditor.dll, fsystem.dll,
FastBusiness.Crypto.dll...). Toàn bộ code trong repo này do Claude viết mới dựa trên mô
tả chức năng bằng lời và ảnh chụp màn hình bạn cung cấp.

Ngoại lệ duy nhất: `Libs/FastBusiness.Crypto.dll` — DLL của chính bạn (từ bản cài FCode
của bạn), được Bcode **tham chiếu như 1 thư viện bình thường và chỉ gọi API public** của
nó (`Crypto.RSADecrypt`, `Crypto.Encode`...), giống hệt cách FCode.exe tự nó dùng — không
decompile/patch gì cả. Xem `Libs/README.md`.

## Cập nhật gần đây

- **Bcode.App (File Lookup preview + Add Script/Script Cart): banner cảnh báo file thay đổi
  từ máy khác + Reload, giống hệt tính năng vừa thêm ở BcodeViewer.** Tính năng banner "file
  thay đổi từ máy khác" ở mục ngay dưới đây trước chỉ có ở BcodeViewer (app riêng) — các màn
  hình mở file bên trong Bcode.App (khung xem trước File Lookup, tab Add Script, popup xem
  entity include) dùng `ScriptEditorControl` riêng, không đi qua BcodeViewer nên không có cảnh
  báo này, dễ bị ghi đè mất thay đổi khi 2 máy cùng sửa 1 file. Porting cùng cơ chế sang
  `ScriptEditorControl`: 1 `System.Windows.Forms.Timer` 4 giây poll `File.GetLastWriteTimeUtc`
  của `CurrentPath`, so với thời điểm nội dung được `LoadContent`/`MarkSaved` gần nhất (2 mốc
  ghi lại thời điểm "coi là đồng bộ với đĩa") — khác thì hiện thanh màu hổ phách (thêm mới,
  nằm dưới thanh path/"(nội dung tạm)" có sẵn) với nút **Reload** (đọc lại file từ đĩa qua
  `LoadContent`, có cảnh báo rõ trong text nếu tab đang dirty vì Reload sẽ mất thay đổi chưa
  lưu) và nút ✕ (bỏ qua đúng lần thay đổi đó, không hỏi lại nữa trừ khi file đổi tiếp lần nữa).
  `MainForm.SaveActiveScript()` không cần sửa gì — `MarkSaved()` (được gọi sẵn sau mỗi lần
  save) tự làm mới mốc thời gian, đúng như cơ chế cũ đã có cho `IsDirty`.
- **BcodeViewer: banner cảnh báo khi file đang mở bị thay đổi từ máy khác, có nút Reload.**
  Trước đó nếu 1 file đang mở trong BcodeViewer bị máy khác (đồng đội, hoặc chính bạn mở 2 cửa
  sổ) ghi đè, BcodeViewer không hề biết — vẫn hiện bản cũ, và nếu bạn bấm Save sẽ ghi đè mất bản
  mới đó mà không cảnh báo gì. Thêm `EditorBridge.GetFileWriteTimeUtc(path)` (trả chuỗi ISO thay
  vì tick số — số tick của `DateTime` vượt quá giới hạn số nguyên an toàn của JS nên marshal qua
  WebView2 sẽ bị làm tròn sai, so sánh chuỗi thì không sao); `editor.js` poll hàm này mỗi 4 giây
  cho file đang mở, so với thời điểm mình load/save gần nhất — khác thì hiện banner màu hổ phách
  (khác màu đỏ của 2 banner lỗi có sẵn, vì đây không phải lỗi nội dung file) có nút **Reload**
  (đọc lại nội dung mới nhất từ đĩa, mất thay đổi chưa lưu nếu có — có cảnh báo rõ trong text nếu
  đang dirty) và nút ✕ (bỏ qua đúng phiên bản đó, vẫn báo lại nếu có bản mới hơn nữa sau đó).
- **Fix: banner "Some fields is duplicate in declare" báo nhầm dù không thấy field nào lặp lại.**
  Bản trước đếm trùng tên `<field name="...">` trên TOÀN BỘ file — nhưng 1 file XML của FCode
  thường có nhiều `<fields>...</fields>` riêng (grid chính + grid con/detail lồng bên trong),
  và việc dùng lại cùng tên field (hay gặp nhất là PK ẩn `stt_rec`) ở các block khác nhau là
  chuyện bình thường, không phải khai báo trùng thật. Vì vậy hầu như file nào có từ 2
  grid/view trở lên cũng bị báo trùng nhầm — dòng trùng thật lại nằm ở 1 block `<fields>` khác,
  xa chỗ người dùng đang xem, tưởng banner báo sai. Sửa `editor.js`: giờ chỉ đếm trùng tên
  TRONG CÙNG 1 block `<fields>...</fields>`, không so giữa các block khác nhau nữa — đúng
  nghĩa "duplicate in declare" của FCode. Click vào banner (tính năng thêm lần trước) vẫn nhảy
  tới đúng dòng field trùng đầu tiên, giờ luôn đúng vị trí thật thay vì im lặng không thấy gì.
- **BcodeViewer: mở từ Bcode.App giờ nhóm đúng theo Project (không còn rơi hết vào "#Other").**
  `Program.cs` của BcodeViewer đã hỗ trợ sẵn `args[1]` là tên project để nhóm cây "recent files"
  bên trái, mặc định `"#Other"` nếu không truyền — nhưng cả nút "Edit in BcodeViewer" lẫn
  double-click (File Lookup) bên Bcode.App đều chỉ truyền mỗi đường dẫn file
  (`Process.Start(ViewerExePath, "<path>")`), nên file nào cũng rơi vào "#Other" dù đang mở
  đúng project (vd KOG). Thêm `FileLookupControl.ProjectName` (được `MainForm.OpenFileLookupTab`
  gán = `Workspace.Name` mỗi lần mở/tái sử dụng tab, kể cả khi đổi WS trong lúc tab đang mở) và
  dùng nó ở cả `FileLookupControl.OpenInViewer()` lẫn `MainForm.OpenFileFromLookup()`: giờ chạy
  `Process.Start(ViewerExePath, "<path>" "<ProjectName>")` — BcodeViewer nhận đúng tên project
  và nhóm file vào đúng chỗ thay vì "#Other". Trường hợp không xác định được WS (hiếm, do
  `OpenFileLookupTab` đã chặn khi chưa có Source Path) vẫn rơi về không truyền tham số này, để
  BcodeViewer tự dùng mặc định "#Other" của nó.
- **BcodeViewer: click vào banner cảnh báo lỗi (đỏ) sẽ dẫn tới đúng vị trí lỗi.** Trước đó 2
  banner cảnh báo (`<!ENTITY ... SYSTEM "path">` trỏ tới file không tồn tại, và `<field
  name="X">` khai báo trùng) chỉ hiện thông báo, không bấm được gì ngoài nút ✕ để đóng. Giờ
  bấm vào phần chữ của banner (không tính nút ✕) sẽ điều hướng tới đúng nơi gây lỗi: với lỗi
  "Could not find file" (đường dẫn file include bị thiếu), mở thẳng đường dẫn đó (giống hệt
  double-click file trong cây bên trái — nếu file thật sự không tồn tại thì báo lỗi đọc file
  như bình thường); với lỗi trùng field, nhảy con trỏ tới đúng dòng khai báo `<field name=...>`
  đầu tiên bị trùng trong tài liệu đang mở. Sửa `editor.js` (`validateActive` giờ lưu kèm
  `path`/`line`/`column` cho từng banner thay vì chỉ lưu chuỗi text, thêm hàm
  `goToValidationIssue`) và `style.css` (con trỏ tay + gạch chân chấm chấm cho banner bấm
  được, không đổi banner "chỉ có nút ✕").
- **File Lookup: double-click ưu tiên mở bằng BcodeViewer nếu đã cấu hình sẵn (Edit In).**
  Trước đó double-click 1 file trong cây File Lookup luôn mở vào tab script nội bộ của Bcode
  (`OpenFileInScriptTab`), bất kể `Settings.ViewerExePath` (đường dẫn `BcodeViewer.exe`, cấu
  hình qua nút "Edit in BcodeViewer" ở khung xem trước) đã trỏ sẵn hay chưa — muốn dùng
  BcodeViewer phải chọn file rồi bấm riêng nút đó. Thêm `MainForm.OpenFileFromLookup`: nếu
  `ViewerExePath` đã có và file .exe tồn tại thì double-click chạy thẳng
  `Process.Start(ViewerExePath, "<path>")` (giống hệt logic nút "Edit in BcodeViewer"); chưa
  cấu hình (hoặc `Process.Start` lỗi) thì rơi về đúng hành vi cũ (mở tab script nội bộ). Chỉ
  đổi double-click trong File Lookup — nút "Add Script"/script cart vẫn mở tab nội bộ như cũ.
- **Fix: build lỗi `CS7036`/`CS1061` sau khi bạn `git pull` bản mới của đồng đội.** Đồng đội
  đã viết lại `FileLookupControl` (constructor giờ nhận thêm `ScriptFileService`, và
  `SearchFor(term)` được thay bằng `ShowForMenuItem(sourceRootPath, link, sysId)` — chính xác
  hơn nhiều so với tìm theo tên file, vì dò thẳng `Main\<link>` và toàn bộ chuỗi controller liên
  quan trong `App_Data\Controllers` qua `FileLookupService.BuildTreeForMenuItem`) cùng lúc với
  `ScriptEditorControl` (thêm `ReadOnly`, F12 nhảy tới entity, `Ctrl+G` "Go to", và
  `SqlSyntaxHighlighter` được viết lại để gán `.Rtf` một lần thay vì tô màu từng đoạn — mượt hơn
  nhiều với file dài). `MainForm.cs` mình push trước đó (thêm tab Gen Update, sidebar, debug
  ConnectStr) lại dùng API cũ của `FileLookupControl`, nên 2 thay đổi này đụng nhau và build lỗi.
  Đã sửa `OpenFileLookupTab()`/`OpenWCommandItem()` trong `MainForm.cs` để gọi đúng API mới; đồng
  thời phát hiện `ScriptEditorControl`'s bản mới thiếu 1 dòng gọi `SqlSyntaxHighlighter.
  DisableNativeUndo` (chỉ còn lại trong comment, không còn gọi thật) nên đã thêm lại — nếu không,
  tab Script/File Lookup preview có nguy cơ bị lại đúng lỗi Ctrl+Z hồi trước dù `RawSqlControl`
  vẫn ổn. Build lại 0 lỗi trước khi push. **Bài học cho các lần sau**: từ giờ luôn lấy đúng bản
  file hiện tại trên máy bạn trước khi sửa/push, thay vì tin vào bản mình lưu cục bộ — tránh lặp
  lại việc ghi đè thay đổi của đồng đội.
- **Fix: thanh cây bên trái (SQL Object/WCommand/Mobile) bị phình to chiếm gần hết cửa sổ.**
  `SplitContainer` chính không khai báo `FixedPanel`, nên không có panel nào được "giữ cố định
  chiều rộng" một cách tường minh — cây bên trái đã bị phình ra chiếm gần hết màn hình thay vì
  giữ hẹp như một sidebar. Đã sửa: đặt `FixedPanel = FixedPanel.Panel1` (panel bên trái giữ đúng
  chiều rộng cố định dù cửa sổ resize/maximize) và giảm chiều rộng mặc định xuống 230px (gọn hơn,
  đủ hiện nhãn tab SQL Object/WCommand/Mobile và tên trong cây).
- **Mới: tab "Gen Update" (đóng gói file update theo menu WCommand)** — khác với "Gen Update
  (dòng đã chọn)" cũ (sinh câu lệnh UPDATE từ grid kết quả, `GenUpdateService`/Ctrl+Shift+U) —
  đây là tính năng riêng, tương ứng đúng tab "WCommand > Gen Update" thật của FCode: double-click
  1 menu trong cây WCommand **khi tab Gen Update đang mở/active** sẽ tìm mọi file liên quan
  (mọi phần mở rộng, dò theo tên trong Link của menu, dùng lại đúng `FileLookupService` đã có)
  và hiện thành cây "Source File" có checkbox bên phải; double-click khi tab khác đang mở vẫn
  mở/nhắm vào File Lookup như trước — không đổi hành vi cũ. Check file cần rồi bấm "Add" để đưa
  vào danh sách "gói update" (bên trái, gom được từ nhiều menu khác nhau); nhập Folder Name
  (gợi ý sẵn dạng `<mã dự án>_<tên máy>_<giờ>` giống FCode) + Save At Path (mặc định lấy từ
  `Workspace.WorkingPath`, đã có sẵn field này từ trước); bấm "Create Update File" sẽ copy từng
  file vào `<Save At Path>\<Folder Name>\App_Data\<đường dẫn tương đối>`, giữ nguyên cấu trúc
  thư mục gốc, và báo Result Path. Mở tab bằng nút "Gen Update" trên toolbar hoặc `Ctrl+Shift+G`.
  **Có lược bớt 1 phần**: màn Gen Update thật của FCode còn có mục "Declaration for Generation"/
  "Content for Generation" để khai báo SQL Object rời (Category/File/SELECT FROM/WHERE/Top
  Script/Bottom Script) đi kèm trong gói — phần này không có trong ảnh chụp màn hình nào cho
  thấy dữ liệu thật, và mô tả bạn đưa ra cũng chỉ nói tới phần file, nên bản này chỉ làm phần
  đóng gói file. Báo nếu bạn cần thêm phần khai báo SQL Object đó.
- **Mới (đang thử nghiệm): `Ctrl+Shift+F5` — Debug Decrypt ConnectStr.** Sau khi bạn xác nhận
  bạn có đội phát triển FCode (người viết đã mất, source thất lạc) và đồng ý hướng "gọi thẳng
  DLL thay vì decompile", đã thêm `Libs/FastBusiness.Crypto.dll` (bản của chính bạn) làm tham
  chiếu thư viện bình thường trong `Bcode.App.csproj`, và thêm `MainForm.DebugDecryptConnectStr()`
  — đọc `HKCU\SOFTWARE\FCoder\ConnectStr`, thử gọi 2 hàm public không cần key riêng
  (`Crypto.RSADecrypt(cipherText)` và `Crypto.Encode(s)`), hiện kết quả thô (hoặc lỗi) qua
  MessageBox. Đây CHỈ là bước kiểm tra, chưa nối vào tính năng Ctrl+F5 thật — vì:
  1. Chưa biết hàm nào (nếu có) cho ra kết quả đúng — cần chạy thử thật trên máy bạn.
  2. `FastBusiness.Crypto.dll` build cho .NET Framework cũ, Bcode chạy .NET 8 — lúc build đã
     thấy warning `MSB3277` (xung đột phiên bản `mscorlib` giữa 2 assembly) — dấu hiệu cho thấy
     có thể gọi được (compile OK) nhưng chạy lại lỗi/trả về null vì lớp bảo vệ native trong DLL
     dựa vào cơ chế của .NET Framework CLR, không chắc còn hoạt động đúng dưới CoreCLR (.NET 8).
  **Cần bạn**: chạy Bcode, `Ctrl+Shift+F5`, gửi lại nguyên văn nội dung hộp thoại hiện ra (kể cả
  khi báo lỗi) — từ đó mới biết hướng nào đi tiếp được.
- **Fix thật sự (v2) cho Ctrl+Z (Undo) ở SQL Query/Command/mọi script box có syntax highlight** —
  bản fix trước (toggle `EM_SETUNDOLIMIT` = 0 trong lúc tô màu, trả về 100 sau đó) **không có
  tác dụng**, đúng như bạn báo lại ("vẫn còn lỗi... cứ tô màu text liên tục mà ko thấy tô đậm").
  Lý do: theo đúng tài liệu của Rich Edit, `EM_SETUNDOLIMIT` **xoá sạch toàn bộ hàng đợi
  Undo/Redo** như một tác dụng phụ MỖI LẦN được gọi — không chỉ tắt việc ghi thêm — kể cả thao
  tác gõ chữ thật vừa mới xảy ra. Vì việc tô màu chạy lại sau mỗi 400ms ngừng gõ, nó âm thầm xoá
  sạch lịch sử Undo thật của người dùng sau mỗi lần dừng gõ — đến khi bấm Ctrl+Z thì không còn gì
  thật để undo cả, chỉ thấy hiệu ứng nhấp nháy tô màu lại mà không có gì được hoàn tác.
  Đã sửa triệt để: **tắt hẳn Undo gốc của RichTextBox** (`SqlSyntaxHighlighter.DisableNativeUndo`,
  gọi 1 lần khi tạo control) và thay bằng `UndoRedoTracker` (file mới) — tự lưu snapshot toàn bộ
  text mỗi lần "ngừng gõ" (cùng mốc debounce 400ms với highlighter) và mỗi khi có 1 thao tác biến
  đổi text chủ động (Comment/Uncomment, Default Type, Write Schema chèn script, Open file), Ctrl+Z/
  Ctrl+Y giờ tự bắt và xử lý bằng tracker này thay vì Undo gốc — hoàn toàn không bị ảnh hưởng bởi
  việc tô màu vì tracker chỉ so sánh nội dung text, không quan tâm định dạng màu.
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
