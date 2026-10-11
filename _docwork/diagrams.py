import sys
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch

sys.stdout.reconfigure(encoding='utf-8')
plt.rcParams['font.family'] = 'Segoe UI'
OUT = r'D:\Bee\Tool\Bcode\_docwork'

C = dict(app='#FFF4CC', appE='#B8860B', box='#E8EEFF', boxE='#5B6FD6', lib='#E6F6EA', libE='#2E8B57',
         data='#FDE8E8', dataE='#C0392B', ext='#EEEEEE', extE='#666666', web='#E3F2FD', webE='#1976D2',
         grp='#FAFAFA', grpE='#BBBBBB')


def canvas(w, h, figw, figh):
    fig, ax = plt.subplots(figsize=(figw, figh))
    ax.set_xlim(0, w); ax.set_ylim(0, h); ax.axis('off'); ax.invert_yaxis()
    return fig, ax


def box(ax, x, y, w, h, text, kind='box', fs=9, bold=False, ha='center', rounding=1.2):
    fc, ec = C[kind], C.get(kind + 'E', '#555')
    ax.add_patch(FancyBboxPatch((x, y), w, h, boxstyle=f'round,pad=0,rounding_size={rounding}', fc=fc, ec=ec, lw=1.3))
    tx = x + w / 2 if ha == 'center' else x + 1.2
    ax.text(tx, y + h / 2, text, ha=ha, va='center', fontsize=fs, fontweight='bold' if bold else 'normal', linespacing=1.35)


def group(ax, x, y, w, h, title, kind='app', fs=11):
    fc, ec = C[kind], C.get(kind + 'E', '#555')
    ax.add_patch(FancyBboxPatch((x, y), w, h, boxstyle='round,pad=0,rounding_size=1.5', fc=fc, ec=ec, lw=1.6, alpha=0.55))
    ax.text(x + 1.5, y + 2.2, title, ha='left', va='center', fontsize=fs, fontweight='bold', color='#333')


def arrow(ax, p1, p2, label=None, color='#333', style='->', ls='-', both=False, fs=8, lpos=0.5, loff=(0, 0), rad=0.0):
    a = FancyArrowPatch(p1, p2, arrowstyle='<->' if both else style, mutation_scale=12, lw=1.3, color=color,
                        linestyle=ls, connectionstyle=f'arc3,rad={rad}')
    ax.add_patch(a)
    if label:
        mx = p1[0] + (p2[0] - p1[0]) * lpos + loff[0]
        my = p1[1] + (p2[1] - p1[1]) * lpos + loff[1]
        ax.text(mx, my, label, ha='center', va='center', fontsize=fs, color=color,
                bbox=dict(fc='white', ec='none', pad=1.2, alpha=0.9))


# ---------------------------------------------------------------- A. Tổng quan hệ thống
def diag_overview():
    fig, ax = canvas(100, 66, 17, 11.2)
    ax.text(50, 2, 'Bộ công cụ Bcode — sơ đồ tổng quan (solution Bcode.sln)', ha='center', fontsize=14, fontweight='bold')

    group(ax, 2, 6, 44, 34, 'Bcode.exe  (src/Bcode.App — WinForms .NET 8 + WebView2)')
    box(ax, 4, 11, 40, 5.5, 'Forms/MainForm (+ .Session / .Hibernate / .ToolGroups)\nthanh trên · cột icon · thanh tool · tab tài liệu · phím tắt', fs=9)
    box(ax, 4, 18.5, 19, 7, 'Controls/  (tab công cụ)\nUserControl bọc 1 trang\nWebBarHost / WebFrameHost', fs=8.5)
    box(ax, 25, 18.5, 19, 7, 'Forms/  (hộp thoại)\nWebDialogForm + trang HTML\nhoặc WinForms thuần', fs=8.5)
    box(ax, 4, 27.5, 19, 6, 'Services/  (nghiệp vụ, SQL,\nfile, API, báo cáo...)', fs=8.5)
    box(ax, 25, 27.5, 19, 6, 'Web/Shell/*.html  (~90 trang)\n+ UI/ (theme, scale, template)', 'web', fs=8.5)
    ax.text(24, 37.5, 'Models/ · Templates/ (mẫu file, mẫu LCTT/CĐKT, mẫu API) · Assets/ · Libs/', ha='center', fontsize=8, color='#555')

    group(ax, 54, 6, 44, 34, 'BcodeViewer.exe  (src/BcodeViewer.App — WinForms + Monaco)')
    box(ax, 56, 11, 40, 5.5, 'MainForm.cs (host cửa sổ, cây Project, sidebar Claude/Gemini,\nsingle-instance Mutex + pipe BcodeViewer.SingleInstance.<session>)', fs=8.5)
    box(ax, 56, 18.5, 19, 7, 'Host/EditorBridge\n(~66 hàm Begin*/Notify*/Get*)\n+ AsyncHostCall', fs=8.5)
    box(ax, 77, 18.5, 19, 7, 'Host/ services\nClaudeChat · SqlRunner · SqlSchema\nWorkspaceSearch · IncludeTree...', fs=8.5)
    box(ax, 56, 27.5, 19, 6, 'Settings/ (lưu cấu hình,\nHint Code, lịch sử, template)', fs=8.5)
    box(ax, 77, 27.5, 19, 6, 'Web/*.js  (Monaco: editor,\ncompletion, entity, dirpreview...)', 'web', fs=8.5)
    ax.text(76, 37.5, 'UI/ (ThemeCatalog, VsCodeThemeImporter) · Assets/snippets · Config/*.txt (prompt AI)', ha='center', fontsize=8, color='#555')

    # Giữa
    box(ax, 4, 43, 19, 8, 'Bcode.ScreenDesigner.exe\n(src/Bcode.ScreenDesigner)\nThiết kế màn hình Dir', 'app', fs=8.5, bold=False)
    box(ax, 27, 43, 19, 8, 'Bcode.KeyGen.exe\n(src/Bcode.KeyGen)\nCấp key bản quyền theo máy', 'app', fs=8.5)

    # Libs
    group(ax, 54, 43, 44, 16, 'Thư viện nhúng vào Bcode.exe (Bcode.App/Libs/*.dll)', 'lib', fs=9.5)
    box(ax, 56, 48, 20, 4.5, 'Bcode.ReportBuilder.dll\n(src/Bcode.ReportBuilder)', 'lib', fs=8)
    box(ax, 78, 48, 18, 4.5, 'ExcelToFrx.dll\n+ FrxGenerator.exe', 'lib', fs=8)
    box(ax, 56, 53.5, 20, 4.5, 'SqlDecryptor.Core.dll', 'lib', fs=8)
    box(ax, 78, 53.5, 18, 4.5, 'FastBusiness.Crypto.dll', 'lib', fs=8)

    # Data
    box(ax, 4, 55, 42, 8, '%AppData%\\Bcode\\  settings.json (DÙNG CHUNG), viewer-*.json, session, Notes, AdvanceNotes,\nFileLookupCache, query-history, sql-history, ApiProjects, license.key, ai-history.json...', 'data', fs=8)
    ax.text(25, 64.8, 'Source UNC (App_Data\\Controllers, Main) · SQL Server (App/Sys data, FSG_A) · claude.ai/gemini · API Anthropic/Gemini · LT3 API',
            ha='center', fontsize=8.5, color='#444', fontweight='bold')

    # arrows
    arrow(ax, (46, 20), (54, 20), 'Process.Start\nfile + project', both=False, color='#B8860B', loff=(0, -2.6))
    arrow(ax, (54, 24), (46, 24), 'pipe Bcode.Control.<session>\nsql|', color='#C0392B', loff=(0, 2.8))
    arrow(ax, (14, 40), (14, 43), None, color='#B8860B')
    ax.text(15.5, 41.6, 'Process.Start --source --project --controller', fontsize=7.8, color='#B8860B', va='center')
    arrow(ax, (50, 40), (50, 43), None, color='#2E8B57')
    ax.text(51.5, 41.6, 'tham chiếu DLL lúc build', fontsize=7.8, color='#2E8B57', va='center')
    arrow(ax, (36.5, 51), (36.5, 55), None, color='#2E8B57')
    ax.text(38, 53, 'key theo máy → license.key (chung Shared/LicenseCodec.cs)', fontsize=7.8, color='#2E8B57', va='center')
    arrow(ax, (14, 51), (14, 55), None, color='#C0392B', ls='--')
    fig.savefig(OUT + r'\dg_overview.png', dpi=140, bbox_inches='tight', facecolor='white')
    plt.close(fig)


# ---------------------------------------------------------------- B. Kiến trúc Bcode.App
def diag_bcode():
    fig, ax = canvas(100, 78, 17, 13.2)
    ax.text(50, 2, 'Kiến trúc Bcode.App — các lớp và luồng dữ liệu', ha='center', fontsize=14, fontweight='bold')

    # Lớp 1: cửa sổ chính
    group(ax, 2, 5, 96, 15, 'Lớp 1 — Cửa sổ chính  Forms/MainForm*.cs (không dùng DI: service được new ngay trong constructor)', 'app', fs=10)
    box(ax, 4, 10, 22, 8, 'Thanh trên  topbar.html\nFile/Actions · Triển khai (menu)\nTool mở rộng (menu) · Claude · Gemini\nchọn WS + Sys/App', 'web', fs=8)
    box(ax, 28, 10, 20, 8, 'Cột icon trái  iconrail\nSQL Object · WCommand · Mobile\n(IconRailControl)', 'web', fs=8)
    box(ax, 50, 10, 22, 8, 'Thanh tool native  _toolSpecs\n(ToolStrip Flow) + nhóm (ToolGroups)\nCommand Palette Ctrl+P', fs=8)
    box(ax, 74, 10, 22, 8, 'Vùng tab  FlatTabControl\nAddDocumentTab · Session restore\nHibernate · TabSwitcher · statusbar', fs=8)

    # Lớp 2: Controls / Forms
    group(ax, 2, 22, 96, 21, 'Lớp 2 — Giao diện công cụ', 'app', fs=10)
    box(ax, 4, 27, 30, 14, 'Controls/ (tab) — ~50 file\nRawSqlControl (SQL Query, Monaco)\nTableEditControl · SqlQueryControl · LookupControl\nFileLookupControl · WCommandTreeControl\nGenUpdate/AdvanceNote · IncludeCheck · CashFlowCheck\nBbxn · QuickReport · CreateRpt · SqlProfiler ...', fs=7.8, ha='left')
    box(ax, 36, 27, 28, 14, 'Forms/ (hộp thoại) — ~55 file\nWebDialogForm → trang HTML co giãn\nProjects/Connections · WCommandEdit\nAddSource/GrantSource · CopyMulti\nApiDeclaration · UiTemplate · CommandPalette\nReportStudio · QuickList · Catalog', fs=7.8, ha='left')
    box(ax, 66, 27, 30, 14, 'Hạ tầng trang web (Controls/)\nIWebPage  ←  WebBarHost (1 WebView2/trang)\n                       ←  WebFrameHost (1 WebView2 + n iframe,\n                           tiết kiệm RAM)\nWebMenu (menu native theo theme) · WebActionBar\nMonacoPreviewControl (preview File Lookup)', 'web', fs=7.8, ha='left')

    # Lớp 3: giao tiếp
    group(ax, 2, 45, 96, 9, 'Lớp 3 — Giao thức C# ↔ trang HTML', 'web', fs=10)
    ax.text(50, 51.2, 'Trang → C#: window.chrome.webview.postMessage({action, data...}) → WebBarHost.Message / WebDialogForm.OnAction\n'
                     'C# → trang: _web.Call("hàm JS(json)")  ·  mọi trang dùng chung shell.css + dialog.css + autoscale.js (tự co giãn theo UiScale/DPI/Template)',
            ha='center', va='center', fontsize=8.8)

    # Lớp 4: Services
    group(ax, 2, 56, 96, 15, 'Lớp 4 — Services/ (không đụng UI)  +  Models/', 'app', fs=10)
    svc = [
        ('SQL & dữ liệu', 'DbConnectionService (WS hiện tại)\nRawSql · SqlQuery · TableData\nSqlObjectBrowser · SqlHint*\nQueryHistory · SqlHistory · DdlTracking'),
        ('Source & menu', 'WCommandService · FileLookupService\nFileParseCache · EntityCheck\nGenAll · SourceGrant · SourceIndex\nUsageSearch · IncludeCheck'),
        ('Báo cáo / tạo file', 'Rpt/* (Pivot, ExcelTemplate)\nQuickReport · QuickList · CatalogClone\nBbxn (Word) · CashFlowDiagnostic/Cfs*\nReportBuilderModule'),
        ('Tích hợp', 'Api/* (LT3, Postman, DPAPI)\nFsgProjectLookup · EInvoiceSetup\nMailTest · License · ViewerControlServer\nSessionStore · AiHistoryStore'),
    ]
    for i, (t, b) in enumerate(svc):
        box(ax, 4 + i * 23.7, 61, 22.5, 9, t + '\n' + b, fs=7.5)

    ax.text(2, 74.2, 'Dữ liệu ngoài:  SQL Server (App/Sys)  ·  source UNC  ·  %AppData%\\Bcode  ·  FSG_A  ·  API Anthropic/Gemini/LT3  ·  BcodeViewer.exe / ScreenDesigner.exe (Process.Start)',
            fontsize=8.5, color='#444', fontweight='bold')
    # arrows
    for x in (15, 38, 61, 85):
        arrow(ax, (x, 18), (x, 22), None, color='#555')
    arrow(ax, (50, 43), (50, 45), None, color='#555')
    arrow(ax, (50, 54), (50, 56), None, color='#555', both=True)
    fig.savefig(OUT + r'\dg_bcode.png', dpi=140, bbox_inches='tight', facecolor='white')
    plt.close(fig)


# ---------------------------------------------------------------- C. Kiến trúc BcodeViewer
def diag_viewer():
    fig, ax = canvas(100, 70, 17, 11.9)
    ax.text(50, 2, 'Kiến trúc BcodeViewer.App — vỏ WinForms mỏng + toàn bộ giao diện trong trang Monaco', ha='center', fontsize=14, fontweight='bold')

    group(ax, 2, 5, 40, 31, 'C# (WinForms) — luồng UI', 'app', fs=10)
    box(ax, 4, 10, 36, 7, 'Program.cs — single instance (Mutex Local\\ + pipe)\nnhận args[0]=file, args[1]=project; crash handler', fs=8)
    box(ax, 4, 19, 36, 8, 'MainForm.cs (2500 dòng)\nWebView2 chính + 2 sidebar AI (claude.ai/gemini) · cây Project\nHandleShellCommand · theme · About · Clear Structure App', fs=8)
    box(ax, 4, 29, 36, 5.5, 'UI/ThemeCatalog + AppTheme + VsCodeThemeImporter\n(1 bảng màu → WinForms + Monaco + CSS)', fs=8)

    group(ax, 46, 5, 52, 31, 'Cầu nối  Host/EditorBridge.cs  ([ComVisible] host object "host")', 'web', fs=10)
    box(ax, 48, 10, 24, 12, 'Begin*(requestId,…)\nxếp hàng AsyncHostCall (thread-pool,\nCompletionGroup huỷ request cũ)\n→ kết quả: message hostCallResult\n(Web/hostcall.js bọc thành Promise)', fs=7.8)
    box(ax, 74, 10, 22, 12, 'Notify*  (đồng bộ, chỉ đổi state)\nGet*  (đẩy 1 lần: theme, snippets,\nsettings, editor config)\nShellCommand · ResolveUiDialog\nSendSqlToBcode (pipe → Bcode)', fs=7.8)
    box(ax, 48, 24, 48, 10.5, 'Host/ services:  ClaudeChatService (Anthropic + Gemini)  ·  SqlRunnerService (chạy SQL trong file, mặc định chặn ghi)\nSqlSchemaService + WorkspaceConnection (đọc chung settings.json của Bcode)  ·  WorkspaceSearchService (Find in Files)\nIncludeTree (đọc chuỗi include, link sang ScreenDesigner)  ·  CaptionTranslator (v→e)  ·  MenuLauncher (F5)', fs=7.8)

    group(ax, 2, 38, 96, 27, 'Web/ (WebView2) — Monaco + JS thuần, nạp theo thứ tự trong index.html', 'web', fs=10)
    cols = [
        ('Khung & tiện ích', 'shell.js (menu/toolbar/cây file)\nui.js (hộp thoại theo theme)\nkeys.js (phím tắt tuỳ chỉnh)\nsettings.js · templates.js\ntabs.js · tabswitcher.js · quickopen.js\ntheme.js · dock.js · hostcall.js'),
        ('Soạn thảo', 'editor.js (Monaco, tab, model)\nfcode-language.js (cú pháp FCode)\ncontextmenu.js (menu chuột phải,\nCtrl+I, dịch caption, clone)\noutline.js · goto.js (Ctrl+G)\nhistory.js (lịch sử/diff)'),
        ('IntelliSense & kiểm tra', 'completion.js (4 lớp gợi ý + AI ghost)\nentity.js (F12/Peek &Entity;)\nproblems.js + problems-worker.js\nsearch.js (Find/Replace in Files)'),
        ('Xem trước & chạy', 'dirpreview.js + dirview.js (Alt+P Dir)\nmailpreview.js (Alt+P mail mẫu)\nsqlrun.js (Chạy SQL, Ctrl+Enter)\nchat.js (đã gỡ khỏi UI)'),
    ]
    for i, (t, b) in enumerate(cols):
        box(ax, 4 + i * 23.8, 43, 22.6, 20, t + '\n\n' + b, 'box', fs=7.8)

    arrow(ax, (40, 22), (48, 16), 'host.Begin*', both=True, color='#1976D2', loff=(0, -1.8))
    arrow(ax, (50, 36), (50, 38), None, both=True, color='#1976D2')
    ax.text(50, 67.3, 'Lưu trữ: %AppData%\\Bcode\\viewer-*.json (settings, hints, recent files, theme) · history\\ (local history) · hint-cache · Config/*.txt (system prompt AI cạnh exe)',
            ha='center', fontsize=8.5, color='#444', fontweight='bold')
    fig.savefig(OUT + r'\dg_viewer.png', dpi=140, bbox_inches='tight', facecolor='white')
    plt.close(fig)


# ---------------------------------------------------------------- Cây tính năng (ngang)
def tree(title, groups, fname, figw=17, rowh=0.62):
    # groups: [(tên nhóm, [(tính năng, ghi chú ngắn)])]
    n = sum(len(g[1]) for g in groups) + len(groups) * 0.6
    H = n * rowh + 2.2
    fig, ax = plt.subplots(figsize=(figw, H))
    ax.set_xlim(0, 100); ax.set_ylim(0, n + 3.4); ax.axis('off'); ax.invert_yaxis()
    ax.text(50, 0.7, title, ha='center', va='center', fontsize=15, fontweight='bold')
    y = 2.2
    cols = ['#B8860B', '#5B6FD6', '#2E8B57', '#C0392B', '#8E44AD', '#16807A', '#B5651D', '#34495E']
    root_y = (2.2 + (n + 1.8)) / 2
    ax.add_patch(FancyBboxPatch((1, root_y - 1.0), 11, 2.0, boxstyle='round,pad=0,rounding_size=0.5', fc='#FFF4CC', ec='#B8860B', lw=1.6))
    ax.text(6.5, root_y, title.split(' — ')[0], ha='center', va='center', fontsize=10.5, fontweight='bold')
    for gi, (gname, feats) in enumerate(groups):
        col = cols[gi % len(cols)]
        gh = len(feats)
        gy = y + (gh - 1) / 2
        ax.add_patch(FancyBboxPatch((16, gy - 0.55), 20, 1.1, boxstyle='round,pad=0,rounding_size=0.4', fc='white', ec=col, lw=1.6))
        ax.text(26, gy, gname, ha='center', va='center', fontsize=8.8, fontweight='bold', color=col)
        ax.plot([12, 14, 14, 16], [root_y, root_y, gy, gy], color=col, lw=1.1)
        for fi, (f, note) in enumerate(feats):
            fy = y + fi
            ax.plot([36, 38.5], [gy, fy], color=col, lw=0.9)
            ax.text(39, fy, f, ha='left', va='center', fontsize=8.6, fontweight='bold', color='#222')
            ax.text(66, fy, note, ha='left', va='center', fontsize=7.8, color='#555')
        y += gh + 0.6
    fig.savefig(OUT + '\\' + fname, dpi=130, bbox_inches='tight', facecolor='white')
    plt.close(fig)


def diag_trees():
    bcode = [
        ('Khởi động & dự án', [
            ('Projects / Choose Server', 'ProjectPickerForm · ConnectionSettingsForm · EditProjectForm (Ctrl+Shift+P / Ctrl+O)'),
            ('Đồng bộ project FSG', 'Ctrl+F5 · FsgProjectLookupService (DB FSG_A) · FCodeConfigImportService'),
            ('Nhiều cửa sổ / khôi phục phiên', 'Ctrl+Shift+N · MainForm.Session (tab chờ) · Hibernate (chế độ hiệu năng)'),
            ('Backup DB · Create Menu · Refresh Web.config', 'MainForm (menu Settings / Actions)'),
        ]),
        ('Khung giao diện', [
            ('Thanh trên / cột icon / thanh tool / tab', 'topbar.html · iconrail · _toolSpecs · FlatTabControl'),
            ('Quick Access + nhóm tool', 'QuickAccessForm · MainForm.ToolGroups (Alt+1..5)'),
            ('Command Palette · Tab switcher · phím tắt', 'CommandPaletteForm (Ctrl+P) · TabSwitcherForm · ShortcutRegistry'),
            ('Giao diện: theme / Template / tỉ lệ / hiệu năng', 'UiThemes · UiTemplate(+Form) · UiScale · UiOverrides · PerformanceProfile'),
            ('Menu Triển khai · Tool mở rộng', 'DeployToolKeys / ExtToolKeys trong MainForm.cs'),
        ]),
        ('SQL & dữ liệu', [
            ('SQL Query (Monaco, AI, debug từng bước)', 'RawSqlControl · RawSqlService · SqlLineAnalyzer/SqlStepPlanner · sqleditor.html'),
            ('Gợi ý code SQL', 'SqlHintCatalog/Service/Popular · Web/Shell/sqlhints.js'),
            ('Lịch sử SQL & lịch sử sửa object', 'QueryHistoryControl · SqlHistoryForm · DdlTrackingService'),
            ('Table / Command / Lookup', 'TableEditControl · SqlQueryControl · LookupControl · PeriodTableQueryService'),
            ('Kết quả: MultiResultView + menu lưới', 'resultview.html · ResultGridMenu · DataScriptService (Add Script)'),
            ('SQL Object tree / Ai đang dùng / So sánh object', 'SqlObjectTreeControl · UsagesControl · CompareObjectsControl'),
            ('Decrypt SQL Object (cần key)', 'DecryptSqlObjectControl · SqlDecryptor.Core.dll · LicenseService'),
            ('SQL Profiler · Change Owner · Compare Structure', 'SqlProfilerControl · ChangeOwnerForm · CompareStructureForm'),
        ]),
        ('Source FCode & menu ERP', [
            ('WCommand tree + CRUD + Gen Script Menu', 'WCommandTreeControl · WCommandService · WCommandEditForm · AppCommandEditForm'),
            ('File Lookup (+cache, Monaco preview, tìm nội dung)', 'FileLookupControl · FileLookupService · FileParseCache · EntityCheckService'),
            ('File Reference · Check Include', 'FileReferenceControl · IncludeCheckControl/Service (Assets/includecheck)'),
            ('Cấp source / Copy to / Clone file', 'AddSourceForm · GrantSourceForm(SourceGrantService) · CopyMultiFileForm'),
            ('Gen Update (gói) · Note (New)/Advance Note', 'GenUpdatePackageControl · AdvanceNoteControl · GenAllService'),
            ('Tạo nhanh danh mục · Clone danh mục', 'QuickListForm/Service · CatalogCloneForm/Service (Templates/fileSource)'),
        ]),
        ('Báo cáo & kế toán', [
            ('Tạo báo cáo (procedure có sẵn / thiết kế từ bảng)', 'ReportStudioForm · QuickReportControl · Bcode.ReportBuilder.dll'),
            ('Create RPT & XML · Excel → FRX', 'CreateRptControl · Services/Rpt/* · ExcelToFrx.dll (cần key)'),
            ('Check LCTT / CĐKT', 'CashFlowCheckControl · CashFlowDiagnosticService.* · CfsStandard/CfsFast'),
            ('Biên bản xác nhận (Word)', 'BbxnControl · BbxnService · BbxnForms'),
            ('Setup eInvoice (FE) · Check Mail', 'SetupEInvoiceControl · EInvoiceSetupService · CheckMailControl/MailTestService'),
        ]),
        ('API & tích hợp', [
            ('Khai báo API (chuẩn LT3) · Tạo cấu trúc API', 'ApiDeclarationForm · Services/Api/* · ApiSchemaBuilderForm'),
            ('Thiết kế màn hình (exe riêng)', 'LaunchScreenDesigner → Bcode.ScreenDesigner.exe'),
            ('Báo cáo yêu cầu FSG · FSG Yêu cầu · FSG FBO', 'FsgRequirementReportControl · FsgRequirementCrawlerForm · QuickLaunchLoginForm'),
            ('Claude / Gemini (tab web nhúng)', 'AiWebPanel + Shared/AiWebHelper'),
            ('Mở BcodeViewer / nhận lệnh từ BcodeViewer', 'FileLookupControl (Process.Start) · ViewerControlServer (pipe)'),
        ]),
        ('Tiện ích', [
            ('Compare Text · String Beauty · Library · Note', 'CompareTextForm · StringBeautyControl · LibrarySnippetForm · NoteControl'),
            ('Script cart (Add/View/Clear/Save/Copy Script)', 'ScriptFileService · ScriptPopupForm'),
            ('Key bản quyền', 'LicenseKeyForm · LicenseService · Shared/LicenseCodec (Bcode.KeyGen cấp key)'),
        ]),
    ]
    tree('Bcode.App — cây tính năng', bcode, 'dg_tree_bcode.png')

    viewer = [
        ('Soạn thảo lõi', [
            ('Monaco nhiều tab, mở/lưu, giữ encoding gốc', 'editor.js · tabs.js · BeginReadFile/BeginWriteFile'),
            ('Cú pháp FCode XML (JS / T-SQL / CSS lồng)', 'fcode-language.js'),
            ('Outline · Go to Definition · Ctrl+G tag', 'outline.js · goto.js'),
            ('Menu chuột phải FCode', 'contextmenu.js (Goto, Open Config, Clone, Dịch caption, Ctrl+I)'),
            ('Lịch sử cục bộ + diff + phát hiện file đổi', 'history.js · LocalHistoryStore'),
        ]),
        ('IntelliSense (4 lớp)', [
            ('Hint Code + snippet FCodeViewer', 'HintSnippetStore · FcodeXmlSnippets · Assets/snippets'),
            ('Cấu trúc XML + structural ghost', 'completion.js (provideXml, structuralGhost)'),
            ('SQL schema (bảng/cột)', 'SqlSchemaService · completion.js (provideSql)'),
            ('AI ghost text (Claude/Gemini)', 'ClaudeChatService · CompletionPromptConfig (Config/*.txt)'),
            ('Từ điển field f.ma_kh', 'FcodeFieldDictionary (header.xml)'),
        ]),
        ('Entity & kiểm tra', [
            ('F12 / Peek &Entity; xuyên include', 'entity.js · IncludeTree · BeginReadIncludeTree'),
            ('Problems (XML, biến, entity, trùng field)', 'problems.js · problems-worker.js (Web Worker)'),
            ('Find / Replace in Files', 'search.js · WorkspaceSearchService'),
            ('Quick Open Ctrl+P', 'quickopen.js · BeginListFiles'),
        ]),
        ('Xem trước & chạy', [
            ('Xem trước Dir (Alt+P)', 'dirpreview.js · dirview.js (dùng chung với ScreenDesigner)'),
            ('Xem trước mail/SMS mẫu (Alt+P)', 'mailpreview.js'),
            ('Chạy SQL tại con trỏ / Debug trong Bcode', 'sqlrun.js · SqlRunnerService · SendSqlToBcode'),
            ('F5 chạy menu của file', 'MenuLauncher · BeginRunMenu'),
        ]),
        ('Khung & cấu hình', [
            ('Web shell (menu, toolbar, cây Project)', 'shell.js · MainForm.PushShellTree/HandleShellCommand'),
            ('Settings · phím tắt · bố cục · Template mới', 'settings.js · keys.js · templates.js · FileTemplateStore'),
            ('Theme (có import VS Code .json/.vsix)', 'ThemeCatalog · VsCodeThemeImporter · theme.js'),
            ('Sidebar Claude/Gemini + đính kèm file', 'MainForm · Shared/AiWebHelper'),
            ('Hộp thoại theo theme', 'ui.js · EditorBridge.ResolveUiDialog'),
            ('Clear Structure App · Refresh Web.config', 'MainForm'),
        ]),
    ]
    tree('BcodeViewer.App — cây tính năng', viewer, 'dg_tree_viewer.png')


if __name__ == '__main__':
    diag_overview(); diag_bcode(); diag_viewer(); diag_trees()
    print('done')
