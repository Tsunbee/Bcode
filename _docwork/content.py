# Nội dung dùng chung cho build_docx.py và STRUCTURE.md  (mỗi dòng bảng: (tính năng, file chính, ghi chú))

TODAY = '11/10/2026'

CONTEXT = [
    'Bộ Bcode gồm 4 chương trình (.exe) + 1 thư viện nhúng, cùng nằm trong solution Bcode.sln (D:\\Bee\\Tool\\Bcode), đều xoay quanh FastBusiness ERP và định dạng file điều khiển "FCode" (controller XML kiểu Dir/Grid/Filter/Report, SQL và JavaScript nhúng bên trong).',
    'Bcode.App (Bcode.exe) — bộ công cụ quản trị/triển khai ERP, bản dựng lại của FCode: SQL Query/Table/Command, WCommand, File Lookup, Gen Update, Advance Note, API LT3, tạo báo cáo, check LCTT/CĐKT, biên bản Word... Gần như mọi màn hình là một trang HTML trong WebView2 (Web/Shell/*.html), C# chỉ giữ dữ liệu và nghiệp vụ.',
    'BcodeViewer.App (BcodeViewer.exe) — trình soạn file controller FCode xây trên Monaco: IntelliSense nhiều lớp, F12/Peek entity, xem trước Dir/mail, chạy SQL, AI ghost text. Từ bản này cả menu/toolbar/cây file cũng vẽ trong trang (Web/shell.js) — WinForms chỉ còn là vỏ.',
    'Bcode.ScreenDesigner (BcodeScreenDesigner.exe) — thiết kế/xem trước màn hình Dir có dữ liệu mẫu; Bcode mở từ menu "Triển khai" hoặc chuột phải cây WCommand. Bcode.KeyGen — công cụ riêng của người phát hành, cấp key bản quyền gắn theo máy. Bcode.ReportBuilder — thư viện (DLL) "Tạo báo cáo" Bcode tham chiếu.',
    'Chia sẻ giữa các chương trình: (1) %AppData%\\Bcode\\settings.json (workspace, kết nối CSDL) do Bcode ghi, BcodeViewer/ScreenDesigner đọc; BcodePaths.cs là bản sao giống hệt ở mỗi app để chọn đúng thư mục ghi được. (2) Code link bằng <Compile Include> chứ không chép: Shared/AiWebHelper.cs (Bcode + Viewer), Shared/LicenseCodec.cs (Bcode + KeyGen), Host/IncludeTree.cs (Viewer + ScreenDesigner), Monaco/theme.js/fcode-language.js của Viewer (Bcode dùng cho preview File Lookup).',
    'Liên lạc giữa tiến trình: Bcode → Viewer bằng Process.Start (file + tên project); Viewer → Bcode qua named pipe Bcode.Control.<session> bằng lệnh "sql|<b64 script>|<b64 tiêu đề>" ("Debug trong Bcode" mở tab SQL Query); Bcode còn xử lý lệnh "fsg|<file>|<project>" nhưng Viewer hiện KHÔNG gửi (F5 của Viewer mở trình duyệt qua Host/MenuLauncher) — Services/ViewerControlServer.cs; Bcode → ScreenDesigner bằng Process.Start --source --project --controller; Viewer chạy 1 bản mỗi phiên Windows (Mutex + pipe BcodeViewer.SingleInstance.<session>).',
]

COMPARE = [
    ('Vai trò', 'Bộ công cụ quản trị / triển khai ERP (bản dựng lại FCode)', 'Trình soạn file controller FCode, gõ code hiện đại'),
    ('Công nghệ', 'WinForms .NET 8 + ~90 trang WebView2 (Web/Shell)', 'WinForms .NET 8 (vỏ mỏng) + Monaco/JS trong 1 WebView2'),
    ('Host ↔ JS', 'postMessage {action,data} → WebBarHost.Message / WebDialogForm.OnAction; C# gọi _web.Call("js")', 'Host object COM EditorBridge: Begin*(requestId,..) + message hostCallResult (Promise ở hostcall.js)'),
    ('Mở nhiều bản', 'Nhiều cửa sổ song song (Ctrl+Shift+N), mỗi cửa sổ 1 project', '1 bản duy nhất mỗi phiên Windows (Mutex + pipe)'),
    ('Mạnh ở', 'SQL, WCommand, File Lookup, Gen Update/Advance Note, API, báo cáo, LCTT', 'IntelliSense, entity F12, Alt+P xem Dir/mail, Ctrl+G, AI ghost text, chạy SQL trong file'),
    ('Dữ liệu dùng chung', '%AppData%\\Bcode\\settings.json (workspace + kết nối) — khai một lần dùng cho cả hai', ''),
]

PROJECTS = [
    ('src/Bcode.App', 'Bcode.exe', 'WinExe net8.0-windows', '214 .cs + ~90 trang Web/Shell; tham chiếu Libs/*.dll, build kèm ScreenDesigner (chép vào ScreenDesigner\\ cạnh exe)'),
    ('src/BcodeViewer.App', 'BcodeViewer.exe', 'WinExe', '30 .cs + ~30 file Web (Monaco bundle ở Web/vs — vendor, bỏ qua)'),
    ('src/Bcode.ScreenDesigner', 'BcodeScreenDesigner.exe', 'WinExe', 'DesignerBridge + Web/designer.*; dùng lại dirpreview.js/entity.js của Viewer'),
    ('src/Bcode.ReportBuilder', 'Bcode.ReportBuilder.dll', 'Library (ConfuserEx khi Release)', 'Module "Tạo báo cáo" kiểu Power BI; build tự chép vào Bcode.App/Libs; extern alias "rb"'),
    ('src/Bcode.KeyGen', 'Bcode.KeyGen.exe', 'WinExe', 'Cấp key bản quyền theo máy (chỉ người phát hành)'),
    ('src/Shared', '(file link)', '—', 'AiWebHelper.cs (nhúng claude.ai/gemini), LicenseCodec.cs (bcrypt(SHA256(key|máy|IP)))'),
    ('tools/lt3-api-docs', '(node)', 'fetch.mjs + convert.mjs', 'Tải tài liệu API chuẩn LT3 → Templates/Api/lt3-standard.json'),
]

# ----------------------------------------------------------------- Bcode.App
BCODE = [
 ('1. Khởi động, dự án, cửa sổ', [
  ('Projects / Choose Server / Edit Project', 'Forms/ProjectPickerForm.cs, ConnectionSettingsForm.cs, EditProjectForm.cs; Web/Shell/projects.html, connections.html, editproject.html; Services/DbConnectionService.cs; Models/Workspace.cs', 'Ctrl+Shift+P / Ctrl+O. Danh sách Workspace nằm trong AppSettings (settings.json) — BcodeViewer đọc lại. Mỗi workspace có 2 DB: Sys Data / App Data.'),
  ('Đồng bộ project từ FSG', 'Services/FsgProjectLookupService.cs, FCodeConfigImportService.cs', 'Ctrl+F5, 3 chế độ (thêm 1 mã / chỉ thêm mới / ghi đè). Đọc DB FSG_A (nbdmda, nbdmserver). Create Menu = exec ns_createCommand.'),
  ('Nhiều cửa sổ; khôi phục phiên; ngủ đông tab SQL', 'Forms/MainForm.Session.cs, MainForm.Hibernate.cs; Services/SessionStore.cs; Controls/SqlTabSnapshot.cs; UI/PerformanceProfile.cs', 'Ctrl+Shift+N. Chỉ tab đang chọn dựng ngay, tab SQL khác là tab "chờ" (LazySql). Chế độ hiệu năng Thấp/TB/Cao/Tự động.'),
  ('Backup DB, Clear Structure App, Refresh Web.config, Create Menu', 'Forms/MainForm.cs (BackupDatabaseAsync, ClearStructureApp, RefreshWebConfig, CreateMenuAsync)', 'Restore/Attach chỉ hiện gợi ý T-SQL (chưa làm).'),
  ('Key bản quyền', 'Forms/LicenseKeyForm.cs; Services/LicenseService.cs, LicenseSecret.cs; Shared/LicenseCodec.cs; src/Bcode.KeyGen', 'Mở khoá Decrypt SQL Object, Create RPT & XML, Excel → FRX, Tạo báo cáo (MainForm.RequireLicense). Mã máy = TÊN_MÁY|IP.'),
 ]),
 ('2. Khung giao diện', [
  ('Thanh trên / cột icon / thanh trạng thái', 'Web/Shell/topbar.html, iconrail.html, statusbar.html; Controls/IconRailControl.cs, WebBarHost.cs', 'Topbar: Settings, Actions, Script (Add/View/Clear/Save/Copy), Triển khai, Tool mở rộng, Claude, Gemini, chọn WS + Sys/App, theme, Quick Access.'),
  ('Thanh công cụ + Quick Access + nhóm tool', 'Forms/MainForm.cs (_toolSpecs), MainForm.ToolGroups.cs, QuickAccessForm.cs; Web/Shell/quickaccess.html; Models/ToolBarGroup.cs', 'Mỗi tool = (key, nhãn, chữ phím tắt, action). Alt+1..5 thả menu nhóm. Menu "Triển khai"/"Tool mở rộng" khai ở DeployToolKeys/ExtToolKeys.'),
  ('Command Palette, chọn tab nhanh, tab', 'Forms/CommandPaletteForm.cs, TabSwitcherForm.cs; Controls/FlatTabControl.cs; Web/Shell/commandpalette.html', 'Ctrl+P; giữ Ctrl rồi Tab = hộp chọn tab; chuột phải vùng tab = menu "Mở nhanh".'),
  ('Phím tắt cấu hình được', 'UI/ShortcutRegistry.cs', 'Phạm vi App/Editor/Tree/Grid. Tool tự đăng ký "tool:<key>" (mặc định Ctrl+Shift+<chữ>) qua SetTools.'),
  ('Theme, Template giao diện, tỉ lệ, DPI, tuỳ chỉnh HTML/CSS', 'UI/UiThemes.cs, ThemeManager.cs, ColorPalette.cs, UiTemplate.cs, UiScale.cs, UiOverrides.cs, DpiForm.cs; Forms/UiTemplateForm.cs, UiScaleForm.cs; Web/Shell/uitemplate.html, ui-designer.js, autoscale.js, shell.css, dialog.css', 'Giữ Shift khi mở = chế độ an toàn (bỏ CSS/HTML ghi đè). Template chỉnh font/màu/vị trí nút từng vùng.'),
  ('Hạ tầng trang web', 'Controls/IWebPage.cs, WebBarHost.cs, WebFrameHost.cs, WebActionBar.cs, WebMenu.cs; Forms/WebDialogForm.cs; UI/WebViewEnvironment.cs', 'WebFrameHost = 1 WebView2 + nhiều iframe (framehost.html) để bớt RAM. WebMenu = menu native theo theme. WebViewEnvironment: 1 môi trường WebView2 dùng chung + phím tắt toàn cục.'),
 ]),
 ('3. SQL & dữ liệu', [
  ('SQL Query (Ctrl+Shift+Q)', 'Controls/RawSqlControl.cs (2400 dòng), SqlResultTabs.cs, MultiResultView.cs; Services/RawSqlService.cs; Web/Shell/sqleditor.html, sqlquerybar.html, resultview.html, sqlmessages.html, sqlpivot.html, sqlresulttabs.html', 'Editor là Monaco trong sqleditor.html. Panel Message, nhiều result set, AI ghost + Ctrl+I, Lịch sử AI (Forms/AiHistoryForm, Services/AiHistoryStore), kéo thả file.'),
  ('Debug từng bước', 'Services/SqlLineAnalyzer.cs, SqlStepPlanner.cs, DebugTargetScanner.cs; Forms/ChooseDebugTargetForm.cs, StepParamsForm.cs', 'F10 / Ctrl+F10 / Shift+F5; chạy trong transaction rollback.'),
  ('Gợi ý code SQL', 'Services/SqlHintCatalog.cs, SqlHintService.cs, SqlHintPopular.cs, SqlFieldTransform.cs; Web/Shell/sqlhints.js, sqlhints-help.html; Controls/SqlHintsHelpControl.cs', 'Provider Monaco, hook onDidType; dữ liệu procedure/function/options lấy từ DB đang chọn. Xem memory bcode-sql-hints-gotchas.'),
  ('Lịch sử SQL / lịch sử sửa object', 'Controls/QueryHistoryControl.cs; Services/QueryHistoryService.cs, SqlHistoryService.cs, DdlTrackingService.cs; Forms/SqlHistoryForm.cs; Web/Shell/queryhistory.html, sqlhistory.html', 'Ctrl+Shift+Y. Ghim câu hay dùng; so sánh phiên bản procedure bằng Monaco diff.'),
  ('Table (Ctrl+Shift+T)', 'Controls/TableEditControl.cs; Services/TableDataService.cs, DataScriptService.cs; Forms/ListEditorForm.cs, RowDetailForm.cs, GotoColumnForm.cs; Web/Shell/tablebar.html, tablestruct.html', 'Sửa dữ liệu kiểu Excel; luôn kẹp TOP ≤ 500; Add Script = DELETE + INSERT; F1 chi tiết dòng, F3 khai báo nhanh danh sách.'),
  ('Command / Lookup', 'Controls/SqlQueryControl.cs, LookupControl.cs; Services/SqlQueryService.cs, LookupService.cs, PeriodTableQueryService.cs; Web/Shell/commandquery.html, lookup.html', 'Command tự chèn TOP 500, tự UNION bảng kỳ $000000, khung Fields bên trái.'),
  ('Menu chuột phải kết quả + Gen Insert/Update', 'Controls/ResultGridMenu.cs; Services/GenInsertService.cs, GenUpdateService.cs, ColumnHeaderGuesser.cs', 'Ctrl+Shift+U = Gen Update (Result).'),
  ('SQL Object tree, Ai đang dùng, So sánh object', 'Controls/SqlObjectTreeControl.cs, UsagesControl.cs, CompareObjectsControl.cs; Services/SqlObjectBrowserService.cs, UsageSearchService.cs, SourceIndexService.cs, ObjectCompareService.cs; Web/Shell/usages.html, compareobjects.html', 'Ctrl+Alt+U (Ai đang dùng); Ctrl+Shift+J (So sánh object). Cây không tự tải khi ô lọc rỗng.'),
  ('Compare Structure, Change Owner', 'Forms/CompareStructureForm.cs, ChangeOwnerForm.cs; Services/SchemaCompareService.cs, ChangeOwnerService.cs', 'Change Owner = ALTER SCHEMA TRANSFER.'),
  ('Decrypt SQL Object', 'Controls/DecryptSqlObjectControl.cs; Libs/SqlDecryptor.Core.dll; Services/DecryptSql*.cs; Web/Shell/decryptsql.html', 'Cần key bản quyền + sysadmin; DAC; luôn rollback. DecryptSqlObjectConfigService chưa ai gọi.'),
  ('SQL Profiler', 'Controls/SqlProfilerControl.cs (1500 dòng), Services/ProfilerTemplateService.cs; Web/Shell/sqlprofilerbar.html', 'Nhúng cửa sổ Profiler.exe thật bằng SetParent; Ctrl+3.'),
 ]),
 ('4. Source FCode & menu ERP', [
  ('WCommand (cây menu + CRUD)', 'Controls/WCommandTreeControl.cs; Services/WCommandService.cs, AppCommandService.cs, CommandTableService.cs; Forms/WCommandEditForm.cs, AppCommandEditForm.cs, WCommandDuplicateForm.cs, WCommandScriptForm.cs, ScriptPopupForm.cs; Web/Shell/wcommandedit.html, appcommandedit.html', 'New (Insert/F4), Edit F3, Delete F8, Gen Script Menu F12, Check WCommand, Refresh F5; Design = mở ScreenDesigner; Ctrl+F5 chạy menu.'),
  ('File Lookup (Ctrl+Shift+F)', 'Controls/FileLookupControl.cs (1500 dòng), MonacoPreviewControl.cs; Services/FileLookupService.cs, FileParseCache.cs, EntityCheckService.cs; Web/Shell/filelookupbar.html, filelookuppreview.html, filepreview.html, searchbox.html, entitypeek.html; Forms/EntityMultiPeekForm.cs', 'Cây file theo menu, cache theo dự án (%AppData%\\Bcode\\FileLookupCache), tô đỏ entity thiếu, tìm nội dung, Edit in BcodeViewer (Process.Start).'),
  ('Cấp source / Copy to / Clone', 'Forms/AddSourceForm.cs, GrantSourceForm.cs, CopyMultiFileForm.cs, CopySourceStandardForm.cs; Services/SourceGrantService.cs; Web/Shell/addsource.html, grantsource.html, copymulti.html, copysourcestandard.html', 'Kho SourceCollection theo phiên bản; chỉ đọc kho, hỏi trước khi ghi đè.'),
  ('File Reference / Check Include', 'Controls/FileReferenceControl.cs, IncludeCheckControl.cs; Services/FileReferenceService.cs, IncludeCheckService.cs; Assets/includecheck/*.txt; Web/Shell/filereference.html, includecheck.html', 'Check Include so khai báo INCLUDE/IGNORE + options + wcommand9 với catalog từng phiên bản (nhúng làm resource).'),
  ('Gen Update (gói file)', 'Controls/GenUpdatePackageControl.cs; Services/GenAllService.cs; Web/Shell/genupdate.html', 'Ctrl+Shift+G. Cấu trúc gói Script\\app|sys\\*.sql + Web\\... dùng chung với Advance Note.'),
  ('Note / Note (New) = Advance Note', 'Controls/NoteControl.cs, AdvanceNoteControl.cs; Services/NoteService.cs, AdvanceNoteService.cs; Web/Shell/note.html, advnote.html', 'Ctrl+Shift+E / Ctrl+Shift+4. Request lưu %AppData%\\Bcode\\AdvanceNotes\\<ws>.json; "Sync yêu cầu" từ bảng nvphyc.'),
  ('Tạo nhanh danh mục / Clone danh mục', 'Forms/QuickListForm.cs, CatalogCloneForm.cs; Services/QuickListService.cs, QuickListConfigStore.cs, CatalogCloneService.cs, HeaderTranslator.cs, FieldDictionaryService.cs; Templates/fileSource/CreateList, Templates/Catalog; Web/Shell/quicklist.html, catalogclone.html', 'Mẫu là file thường, placeholder [#TEN#]/{{Ten}}; sửa mẫu là đổi kết quả không cần build.'),
 ]),
 ('5. Báo cáo & kế toán', [
  ('Tạo báo cáo', 'Forms/ReportStudioForm.cs; Controls/QuickReportControl.cs; Services/QuickReportService.cs, ReportBuilderModule.cs; src/Bcode.ReportBuilder/*; Templates/fileSource/CreateReport, BuildReport; Web/Shell/quickreport.html, reportmodes.html', 'Menu "Tool mở rộng". 2 chế độ: từ procedure có sẵn / thiết kế từ bảng (DLL). Cần key.'),
  ('Create RPT & XML, Excel → FRX', 'Controls/CreateRptControl.cs; Services/Rpt/*; Libs/ExcelToFrx/; Web/Shell/createrpt.html', 'Cần key. Pivot Excel mẫu (PivotXlsxWriter). ExcelToFrx.dll + FrxGenerator.exe chép ra thư mục ExcelToFrx\\.'),
  ('Check LCTT / CĐKT', 'Controls/CashFlowCheckControl.cs; Services/CashFlowDiagnosticService*.cs, CfsStandard.cs, CfsFast.cs; Templates/Cfs/{standard,fast}; Web/Shell/cfsdiag.html', 'Menu "Triển khai". Mẫu chuẩn TT99; xem memory cfs-standard-templates, fast-indirect-cashflow-proc.'),
  ('Biên bản xác nhận (Word)', 'Controls/BbxnControl.cs; Services/BbxnService.cs, BbxnForms.cs; Web/Shell/bbxn.html', 'Xem memory bbxn-templates-state.'),
  ('Setup eInvoice, Check Mail', 'Controls/SetupEInvoiceControl.cs, CheckMailControl.cs; Services/EInvoiceSetupService.cs, MailTestService.cs, MailSettingsReader.cs; Web/Shell/setupeinvoice.html, checkmail.html', 'Check Mail dùng MailKit; không lưu mật khẩu.'),
 ]),
 ('6. API & tích hợp', [
  ('Khai báo API (chuẩn LT3) / Tạo cấu trúc API', 'Forms/ApiDeclarationForm.cs, ApiSchemaBuilderForm.cs; Services/Api/ApiClient.cs, ApiProjectStore.cs, Lt3Templates.cs, PostmanConverter.cs; Models/ApiProject.cs; Templates/Api/lt3-standard.json; Web/Shell/apideclaration.html, apischema.html', 'Menu "Triển khai". Token trần (không Bearer), mật khẩu DPAPI, nhập/xuất Postman v2.1. Xem memory api-declaration-lt3.'),
  ('Thiết kế màn hình', 'MainForm.LaunchScreenDesigner; src/Bcode.ScreenDesigner/*', 'Exe riêng: tham số --source --project --controller.'),
  ('FSG: báo cáo yêu cầu / FSG Yêu cầu / FSG FBO', 'Controls/FsgRequirementReportControl.cs; Forms/FsgRequirementCrawlerForm.cs, QuickLaunchLoginForm.cs; Web/Shell/fsgreq.html', 'Bung web đăng nhập Workspace.LoginWLink, tự điền user/pass bằng JS injection.'),
  ('Claude / Gemini nhúng', 'Controls/AiWebPanel.cs; Shared/AiWebHelper.cs', 'Tab web claude.ai / gemini, profile riêng.'),
  ('Giao tiếp với BcodeViewer', 'Services/ViewerControlServer.cs; FileLookupControl (mở Viewer); MainForm.RunMenuForFileAsync, OpenViewerScriptTab', 'Pipe Bcode.Control.<session>: "sql|..." đang dùng; "fsg|..." còn handler nhưng Viewer không gửi nữa'),
 ]),
 ('8. Control/helper nền (ít khi phải sửa)', [
  ('Khung xem/sửa script (RichTextBox)', 'Controls/ScriptEditorControl.cs, SqlSyntaxHighlighter.cs, UndoRedoTracker.cs, SuggestPopup.cs; UI/LineNumberGutter.cs', 'Dùng cho script SQL Object, WCommand script popup; Undo tự viết vì RichTextBox tô màu.'),
  ('Lưới & hiển thị', 'UI/GridDisplayHelper.cs, GridDragScroll.cs, ControlPerf.cs, FlatToolStripRenderer.cs, ThemedForm.cs', 'Autosize 1 lần, kéo chọn tự cuộn, double buffer.'),
  ('Tuỳ chọn giao diện lẻ', 'UI/MousePointer.cs (giữ con trỏ chuột khi gõ), TreeColorOptions.cs (màu cây WCommand/File Lookup), IconGlyphs.cs, AppIcons.cs, NativeAppLauncher.cs (mở .rpt/.xlsx bằng app Windows)', 'Cấu hình trong Template giao diện.'),
  ('Form WinForms cũ còn dùng', 'Forms/LookupForm.cs, StringBeautyForm.cs, SetupEInvoiceForm.cs, SimplePromptForm.cs', 'Phần lớn đã có bản tab WebView2 tương ứng; form cũ chỉ còn cho luồng phụ.'),
  ('Rpt/* (chi tiết)', 'Services/Rpt/ExcelTemplateWriter.cs, GridControllerReader.cs, PivotDetector.cs, PivotXlsxWriter.cs, ReportDeployService.cs, ReportProfilerService.cs, RptXmlBuilder.cs, RptModels.cs', 'Chuỗi: profiler → đọc Grid controller → chọn field → sinh Excel mẫu + Report xml.'),
  ('CashFlowDiagnosticService (chia partial)', 'Services/CashFlowDiagnosticService.{Catalog,Engine,Fast,LineByLine,Standard}.cs', 'Engine = tính lại/cặp bút toán; Standard/Fast/LineByLine = 3 kiểu đối chiếu.'),
 ]),
 ('7. Tiện ích', [
  ('Compare Text, String Beauty, Library', 'Forms/CompareTextForm.cs, LibrarySnippetForm.cs; Controls/StringBeautyControl.cs; Services/DiffService.cs, SqlFormatterService.cs, SnippetLibraryService.cs; Web/Shell/stringbeauty.html, library.html', 'DiffService = LCS tự viết.'),
  ('Script cart', 'Services/ScriptFileService.cs; Forms/ScriptPopupForm.cs', 'Add/View/Clear/Save/Copy Script trên topbar; Ctrl+Alt+Shift+A / V.'),
  ('Create *.rpt/*.xlsx, Create Processing, View Rpt in FEC', 'Forms/CreateRptXlsxForm.cs, StubForm.cs; Services/XlsxExportService.cs', 'Create Processing / View Rpt in FEC còn là StubForm (chưa làm).'),
 ]),
]

# ----------------------------------------------------------------- BcodeViewer
VIEWER = [
 ('1. Soạn thảo lõi', [
  ('Monaco nhiều tab, mở/lưu', 'Web/editor.js, tabs.js, index.html; Host/EditorBridge.BeginReadFile/BeginWriteFile/BeginSaveWithHistory', 'Giữ nguyên encoding gốc theo BOM (file .f UTF-16). Lưu kèm snapshot local history.'),
  ('Cú pháp FCode XML', 'Web/fcode-language.js', 'JS / T-SQL / CSS tô màu trong các khối của XML.'),
  ('Outline, Go to Definition, Ctrl+G', 'Web/outline.js, goto.js', 'Ctrl+G liệt kê tag controller; Shift+F12 tìm tham chiếu.'),
  ('Menu chuột phải kiểu FCode', 'Web/contextmenu.js', 'Goto Response/Command/Function (F11), Open File Config, Chạy SQL, Dịch caption (Ctrl+Alt+T), Clone file, Get Hash Source, Ctrl+I (AI).'),
  ('Lịch sử cục bộ + diff + phát hiện file đổi', 'Web/history.js; Settings/LocalHistoryStore.cs; EditorBridge.BeginGetFileHistory/GetHistorySnapshot/GetFileWriteTimeUtc', ''),
  ('Quick Open (Ctrl+P), chọn tab nhanh', 'Web/quickopen.js, tabswitcher.js; EditorBridge.BeginListFiles', 'Danh sách file do host quét (có cache), trang chỉ lọc.'),
  ('Dịch caption v → e', 'Host/CaptionTranslator.cs; EditorBridge.BeginTranslateCaptions', 'Engine google (không key) / gemini / claude chọn ở Settings.'),
 ]),
 ('2. IntelliSense (4 lớp) — Web/completion.js', [
  ('Lớp 1: Hint Code + snippet FCodeViewer', 'Settings/HintSnippetStore.cs, FcodeXmlSnippets.cs; Assets/snippets/*.xml; Web/templates.js', 'Đồng bộ, không gọi mạng. Thư mục dùng chung đặt ở Settings.'),
  ('Lớp 2: cấu trúc XML + structural ghost', 'completion.js (provideXml, structuralGhost); Settings/FcodeFieldDictionary.cs; EditorBridge.GetFieldTemplates', 'Từ điển field (header.xml): gõ f.ma_kh + Enter.'),
  ('Lớp 3: SQL schema', 'Host/SqlSchemaService.cs, WorkspaceConnection.cs; EditorBridge.BeginGetSqlTables/Columns', 'Chỉ chạy khi con trỏ ở vùng SQL; đọc chung workspace của Bcode.'),
  ('Lớp 4: AI ghost text', 'Host/ClaudeChatService.cs; EditorBridge.BeginInlineCompletion; Settings/CompletionPromptConfig.cs (Config/*.txt)', 'Debounce 400 ms; cache 400 ký tự cuối; system prompt sửa trong Config/common|xml|sql|js|css.txt cạnh exe, không cần build.'),
 ]),
 ('3. Entity & kiểm tra', [
  ('F12 / Peek &Entity; xuyên include', 'Web/entity.js; Host/IncludeTree.cs; EditorBridge.BeginReadFiles/BeginReadIncludeTree', 'Độ sâu include tới 64; fallback tìm toàn project; cache theo tên.'),
  ('Problems', 'Web/problems.js, problems-worker.js (Web Worker); dock.js', 'XML sai, số biến [item], thiếu entity/include, trùng field.'),
  ('Find / Replace in Files', 'Web/search.js; Host/WorkspaceSearchService.cs; EditorBridge.BeginSearchWorkspace/BeginReplaceInWorkspace', ''),
 ]),
 ('4. Xem trước & chạy', [
  ('Xem trước Dir (Alt+P)', 'Web/dirpreview.js, dirview.js', 'Dựng form FormTable từ source; file dùng chung với Bcode.ScreenDesigner.'),
  ('Xem trước mail/SMS mẫu (Alt+P ở Message.xml...)', 'Web/mailpreview.js', ''),
  ('Chạy SQL trong file', 'Web/sqlrun.js; Host/SqlRunnerService.cs; EditorBridge.InspectSql/StartSql/PollSql/CancelSql/SendSqlToBcode', 'Ctrl+Enter; ghi INSERT/UPDATE/DELETE/EXEC mặc định TẮT; "Debug trong Bcode" gửi script sang Bcode.'),
  ('F5: chạy menu của file', 'Host/MenuLauncher.cs; EditorBridge.BeginRunMenu', 'Tra menu wcommand rồi mở trình duyệt mặc định.'),
 ]),
 ('5. Khung, cấu hình, AI', [
  ('Web shell (menu, toolbar, cây Project)', 'Web/shell.js; MainForm.cs (ShowWebShell, PushShellTree, HandleShellCommand); EditorBridge.ShellCommand/GetShellState', 'Cây file nhóm theo Project do MainForm giữ rồi đẩy sang trang.'),
  ('Settings, phím tắt, bố cục', 'Web/settings.js, keys.js; Settings/ViewerSettings.cs; EditorBridge.GetSettings/BeginSaveSettings/GetUiPrefs/SetUiPrefs', 'SettingsForm/HintCodeForm/TemplatePickerForm (WinForms) chỉ là dự phòng.'),
  ('Hint Code, New from Template', 'Web/templates.js; Settings/HintSnippetStore.cs, FileTemplateStore.cs', ''),
  ('Theme (+ import VS Code .json/.vsix)', 'UI/ThemeCatalog.cs, AppTheme.cs, VsCodeThemeImporter.cs; Web/theme.js', 'Một bảng màu dùng cho WinForms + Monaco + CSS.'),
  ('Hộp thoại theo theme', 'Web/ui.js; EditorBridge.ResolveUiDialog', 'Thay alert/confirm/MessageBox.'),
  ('Sidebar Claude/Gemini + đính kèm file', 'MainForm.cs; Shared/AiWebHelper.cs', 'Trang web thật (profile riêng), không dùng API key.'),
  ('Một bản mỗi phiên; mở file từ ngoài', 'Program.cs; MainForm.StartPipeServer/OpenExternalRequest; Host/WorkspaceConnection.ResolveProjectName', 'args[0]=file, args[1]=project (nếu thiếu thì suy từ đường dẫn).'),
 ]),
]

SHORTCUTS_BCODE = [
 ('Ctrl+P', 'Command Palette'), ('Ctrl+Shift+N', 'Cửa sổ Bcode mới'), ('Ctrl+Shift+P / Ctrl+O', 'Projects / Choose Server'),
 ('Ctrl+F5', 'Synchronize project (FSG) / ở cây WCommand: chạy menu'), ('Ctrl+Shift+H', 'Ẩn/hiện cây trái'),
 ('Ctrl+Shift+Q L T C W F G R O U E 4 Y J', 'Tool: SQL Query, Lookup, Table, Command, WCommand, File Lookup, Gen Update, File Reference, Change Owner, Gen Update (Result), Note, Note (New), Lịch sử SQL, So sánh object'),
 ('Ctrl+3 / Ctrl+5', 'SQL Profiler / mở Program Path'), ('Ctrl+Alt+U', 'Ai đang dùng object'),
 ('Ctrl+Tab, Ctrl+W, Ctrl+Shift+B', 'Tab kế / đóng / mở lại tab vừa đóng'), ('Alt+1..5', 'Thả menu nhóm tool'),
 ('Ctrl+Alt+Shift+A / V', 'Add Script / View Script Cart'),
 ('Editor SQL: F5, Ctrl+Enter, F7, F12, Ctrl+F12, Alt+Z, Ctrl+I, Alt+\\, Ctrl+Alt+H', 'Chạy, Beauty, xem nhanh object, mở object sang tab mới, wrap, AI, gọi gợi ý AI, lưu vào Lịch sử SQL'),
 ('Debug: F10, Ctrl+F10, Shift+F5', 'Step, chạy tới con trỏ, dừng'),
 ('Lưới Table: F1 F3 F4 F8, Ctrl+I, Ctrl+N, Ctrl+\', Ctrl+0', 'Chi tiết dòng, khai báo nhanh, thêm, xoá, nhân bản, chèn sau, copy giá trị, set NULL'),
 ('Khai báo API: Ctrl+Shift+Enter / Ctrl+S / Ctrl+Shift+T / F / K', 'Gửi / lưu / token / format / validate'),
]
SHORTCUTS_VIEWER = [
 ('F12 / Ctrl+click', 'Đi tới / Peek &Entity;'), ('Ctrl+G', 'Go to tag'), ('Ctrl+P', 'Quick Open'), ('Alt+P', 'Xem trước Dir / mail'),
 ('Ctrl+Enter', 'Chạy SQL tại con trỏ'), ('Ctrl+I', 'AI sinh/sửa code'), ('F5', 'Lưu + chạy menu của file'), ('F11', 'Goto Response/Command/Function'),
 ('Shift+F12', 'Tìm tham chiếu'), ('Ctrl+Alt+T', 'Dịch caption v → e'), ('Giữ Ctrl rồi Tab', 'Hộp chọn tab'),
 ('Khác', 'Mọi phím tuỳ chỉnh được ở Settings → Phím tắt (Web/keys.js, BCODE_COMMANDS)'),
]

DATA = [
 ('%AppData%\\Bcode\\settings.json', 'Bcode', 'AppSettings: workspace, kết nối, đường dẫn, UiScale, FileLookupCacheMode, tool order/nhóm... Viewer + Designer đọc.'),
 ('…\\viewer-*.json, viewer-hints.json, viewer-recent-files.json, viewer-theme(s)', 'Viewer', 'ViewerSettings, Hint Code, file gần đây, theme (cũng có thư mục viewer-themes, hint-cache, history).'),
 ('…\\FileLookupCache\\<dự án>.json, file-index, menu-cache, def-cache, sql-objects, source-index', 'Bcode', 'Cache phân tích file/menu/object (xoá được).'),
 ('…\\AdvanceNotes\\, Notes\\, session\\, sql-history\\, query-history\\, quickreport-backup\\, Backups\\, Traces\\', 'Bcode', 'Dữ liệu người dùng: Request, ghi chú, phiên tab, lịch sử SQL.'),
 ('…\\ApiProjects\\*.json, ApiProfiles', 'Bcode', 'Hồ sơ API (mật khẩu DPAPI), profile cũ.'),
 ('…\\ai-history.json, ui-template.json, ui-themes, sqlbar_layout.json, einvoice.json, checkmail.json, fsg-requirements.json, license.key', 'Bcode', 'Lịch sử AI, Template giao diện, theme, bố cục thanh SQL, cấu hình eInvoice/mail, key bản quyền.'),
 ('…\\crash.log, hint-error.log, apply-timing.log', 'cả hai', 'Log chẩn đoán.'),
 ('<exe>\\Config\\common|xml|sql|js|css.txt', 'Viewer', 'System prompt AI ghost text (tự sinh lần đầu).'),
 ('Templates\\ cạnh Bcode.exe', 'Bcode', 'fileSource (CreateList/CreateReport/BuildReport/...), Catalog, Cfs (standard + fast), Api/lt3-standard.json, HeaderDictionary.json.'),
]

RECIPES = [
 ('Thêm 1 công cụ dạng tab (Bcode)', '1) Web/Shell/<ten>.html (dùng shell.css, dialog.css, autoscale.js)  2) Controls/<Ten>Control.cs: UserControl chứa WebBarHost("<ten>.html"), gắn _web.Message / _web.Ready, gọi _web.Call("js")  3) Forms/MainForm.cs: hàm Open<Ten>Tab() (AddDocumentTab) + _toolSpecs.Add(("key","Nhãn","phím"|null, action))  4) muốn ẩn vào menu thì thêm key vào DeployToolKeys / ExtToolKeys  5) logic ở Services/<Ten>Service.cs. Phím tắt, Quick Access, Command Palette tự nhận.'),
 ('Thêm 1 hộp thoại (Bcode)', 'Forms/<Ten>Form.cs kế thừa WebDialogForm (override OnReady / OnAction) + Web/Shell/<ten>.html. Logic nằm ở Services.'),
 ('Thêm cấu hình lưu', 'Models/AppSettings.cs (settings.json). Tool riêng thì tạo file JSON dưới %AppData%\\Bcode\\ như checkmail.json.'),
 ('Sửa editor SQL Query', 'Web/Shell/sqleditor.html (Monaco, gợi ý ở sqlhints.js) + Controls/RawSqlControl.cs; thanh nút: sqlquerybar.html + Models/SqlBarLayout.cs.'),
 ('Sửa File Lookup', 'Cây/tìm: FileLookupService.cs (BuildTreeForMenuItem), cache: FileParseCache.cs, UI: FileLookupControl.cs + filelookupbar.html.'),
 ('Sửa phím tắt mặc định (Bcode)', 'UI/ShortcutRegistry.cs (BuildFixed); tool: chữ trong _toolSpecs của MainForm.cs.'),
 ('Thêm hàm host cho Viewer', 'Host/EditorBridge.cs: thêm Begin<Ten>(requestId, ...) chạy qua AsyncHostCall; phía trang gọi bcodeHost.call("<Ten>", ...) (Web/hostcall.js). Hàm chỉ đổi state trong RAM thì dùng Notify*/Get* đồng bộ.'),
 ('Thêm phím tắt / lệnh menu cho Viewer', 'Web/keys.js (BCODE_COMMANDS) + Web/shell.js (menu); lệnh cần C# thì thêm case ở MainForm.HandleShellCommand.'),
 ('Sửa gợi ý / AI ghost của Viewer', 'Web/completion.js; prompt: Config/*.txt (không cần build); gọi API: Host/ClaudeChatService.cs.'),
 ('Sửa xem trước Dir', 'Web/dirpreview.js + dirview.js (ảnh hưởng cả Screen Designer).'),
 ('Sửa Screen Designer', 'src/Bcode.ScreenDesigner/Web/designer.js + DesignerBridge.cs; dữ liệu mẫu: SampleData/*.'),
]

TRAPS = [
 'Bash tool làm mất dấu \\ — script có đường dẫn Windows phải ghi bằng Write (memory bash-backslash-gotcha).',
 'Menu bật từ trang WebView2 tự đóng do AppFocusChange — sửa ở WebMenu.Track (memory bcode-webmenu-focus-bug).',
 'Đừng đặt PacketSize > 16383 (TLS) khi kết nối SQL (memory bcode-sql-connection-gotchas).',
 'Mỗi tab SQL Query ≈ 4 WebView2 ~125 MB → đã gộp bằng WebFrameHost / ngủ đông (memory bcode-startup-ram-findings).',
 'Bcode.ReportBuilder.dll: Release phải build bằng MSBuild.exe cổ điển (ConfuserEx), giữ renameArgs=false; sau đó build lại Bcode.App.',
 'File .f của FastBusiness là UTF-16 — ghi lại phải giữ encoding gốc.',
 'Mọi nội dung trong Web/vs là bundle Monaco (vendor): không sửa, không đọc.',
 'Code chưa gắn vào UI: Services/DecryptSqlObjectConfigService.cs; BcodeViewer UI/CodeHighlighter.cs; Controls/PillButton.cs (Bcode) và UI/PillButton.cs (Viewer) là helper nhỏ; Web/chat.js (chat panel) đã gỡ khỏi index.html.',
 'Mobile (cột icon trái) mới là placeholder; Restore/Attach DB, Create Processing, View Rpt in FEC chưa làm.',
]
