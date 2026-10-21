import os, sys
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch

sys.stdout.reconfigure(encoding='utf-8')
plt.rcParams['font.family'] = 'Segoe UI'
OUT = os.path.dirname(os.path.abspath(__file__))   # ảnh ghi cạnh script (_docwork)

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

    group(ax, 2, 6, 44, 34, 'Bcode.exe  (src/Bcode.App — WinForms .NET 8; 2 chế độ: Web / App)')
    box(ax, 4, 11, 40, 5.5, 'Forms/MainForm (+ .Session / .Hibernate / .ToolGroups / .FsgNotify)\nthanh trên · cột icon · thanh tool · tab · phím tắt · Trợ giúp · chuông FSG', fs=8.6)
    box(ax, 4, 18.5, 19, 7, 'Controls/  (tab công cụ)\nUserControl bọc 1 trang\nWebBarHost / WebFrameHost', fs=8.5)
    box(ax, 25, 18.5, 19, 7, 'Forms/  (hộp thoại)\nWebDialogForm + trang HTML\nhoặc WinForms thuần', fs=8.5)
    box(ax, 4, 27.5, 19, 6, 'Services/  (nghiệp vụ, SQL,\nfile, API, báo cáo...)', fs=8.5)
    box(ax, 25, 27.5, 19, 6, 'Chế độ Web: Web/Shell (80 trang)\nChế độ App: NativePage · NK.*\n· Native*Control (WinForms tự vẽ)', 'web', fs=7.8)
    ax.text(24, 37.5, 'Models/ · Templates/ · Assets/ · Libs/ · docs/HuongDanSuDung.md (tab Trợ giúp)', ha='center', fontsize=8, color='#555')

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
    box(ax, 4, 55, 42, 8, '%AppData%\\Bcode\\  settings.json (DÙNG CHUNG), viewer-*.json, session, Notes, AdvanceNotes,\nFileLookupCache, sql-definitions, sql-history, ApiProjects, license.key, ai-history.json,\nfsg-requirements / fsg-notify.json, weeklymail*, WebView2 (+ hồ sơ phụ WebView2-n)', 'data', fs=7.6)
    ax.text(25, 64.8, 'Source UNC (App_Data\\Controllers, Main, MobileService) · SQL Server (App/Sys data, FSG_A: nvphyc) · claude.ai/gemini · API Anthropic/Gemini · LT3 API · SMTP/IMAP',
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
    fig, ax = canvas(100, 82, 17, 13.9)
    ax.text(50, 2, 'Kiến trúc Bcode.App — các lớp và luồng dữ liệu (2 chế độ giao diện: Web / App)', ha='center', fontsize=14, fontweight='bold')

    # Lớp 1: cửa sổ chính
    group(ax, 2, 5, 96, 15, 'Lớp 1 — Cửa sổ chính  Forms/MainForm*.cs (không dùng DI: service được new ngay trong constructor)', 'app', fs=10)
    box(ax, 4, 10, 22, 8, 'Thanh trên  topbar.html / NativeTopBar\nActions · Triển khai · Tool mở rộng\nClaude · Gemini · Trợ giúp (Ctrl+F1)\nWS + Refresh DB · Sys/App · chuông FSG', 'web', fs=7.6)
    box(ax, 28, 10, 20, 8, 'Cột icon trái  iconrail\nSQL Object · WCommand\nMobile (bảng dvcommand)\n(IconRailControl)', 'web', fs=7.8)
    box(ax, 50, 10, 22, 8, 'Thanh tool  _toolSpecs\n(ToolStrip Flow) + nhóm (ToolGroups)\nCommand Palette Ctrl+P\nShortcutRegistry (Web/App default)', fs=7.6)
    box(ax, 74, 10, 22, 8, 'Vùng tab  FlatTabControl\nAddDocumentTab · Session restore\nHibernate · TabSwitcher · statusbar\nCtrl+O dự án khác → cửa sổ mới', fs=7.6)

    # Lớp 2: Controls / Forms / hạ tầng
    group(ax, 2, 22, 96, 23, 'Lớp 2 — Giao diện công cụ  (UiMode.IsNative chọn nhánh; nghiệp vụ dùng chung)', 'app', fs=10)
    box(ax, 4, 27, 22.5, 16, 'Controls/ (tab) — 101 file\nRawSqlControl (SQL, Monaco)\nTableEdit · SqlQuery · Lookup\nFileLookup (+QuickGrant)\nWCommandTree (+Mobile)\nGenUpdate · AdvanceNote\nFsgRequirementReport · Help\nCashFlow · Bbxn · Profiler…', fs=7.2, ha='left')
    box(ax, 28.3, 27, 22.5, 16, 'Forms/ (hộp thoại) — 62 file\nWebDialogForm → trang HTML\nProjects (+Native) · Connections\nWCommandEdit · AppCommandEdit\nAddSource/GrantSource/SourceView\nQuickList (+AI danh mục)\nRptSchema · PartitionCheck\nFsgNotify · UiTemplate…', fs=7.2, ha='left')
    box(ax, 52.6, 27, 21.5, 16, 'Chế độ Web — hạ tầng trang\nIWebPage ← WebBarHost\n   (1 WebView2 / trang)\n← WebFrameHost (1 WebView2\n   + n iframe, bớt RAM)\nWebMenu · WebActionBar\nMonacoPreviewControl\nWebViewEnvironment (+0x800700AA)', 'web', fs=7.2, ha='left')
    box(ax, 75.9, 27, 20.1, 16, 'Chế độ App — WinForms tự vẽ\nNativePage + Register\n(WebBarHost tự thay trang)\nNK.* · CardPanel · PillButton\nDropDownButton · NativeFindBar\nNativeSql (Scintilla, peek,\n  AI, debug) · Native*Control\nFcodeLexer · SqlHintEngine(Jint)', 'lib', fs=7.2, ha='left')

    # Lớp 3: giao tiếp
    group(ax, 2, 47, 96, 10, 'Lớp 3 — Giao thức C# ↔ giao diện (giống nhau ở 2 chế độ)', 'web', fs=10)
    ax.text(50, 53.4, 'Trang → C#: window.chrome.webview.postMessage({action, data...}) → WebBarHost.Message / WebDialogForm.OnAction\n'
                     'C# → trang: _web.Call("hàm JS(json)")  ·  trang dùng chung shell.css + dialog.css + autoscale.js (co giãn theo UiScale/DPI/Template)\n'
                     'Chế độ App: NativePage.Dispatch("ns.fn", args) nhận lệnh C# · Post(new { action }) gửi lên — cùng tên action nên control chủ không phải sửa',
            ha='center', va='center', fontsize=8.2)

    # Lớp 4: Services
    group(ax, 2, 59, 96, 17, 'Lớp 4 — Services/ (103 file, không đụng UI)  +  Models/', 'app', fs=10)
    svc = [
        ('SQL & dữ liệu', 'DbConnectionService (WS hiện tại)\nRawSql · SqlQuery · TableData\nTableMenu · SqlObjectBrowser\nSqlHint* · SqlDefinitionIndex\nQueryHistory · SqlHistory · DdlTracking'),
        ('Source & menu', 'WCommand · AppCommand (+dvcommand)\nFileLookup · FileParseCache\nMobilePath · SourceGrant · SourceIndex\nGenAll · EntityCheck · IncludeCheck\nUsageSearch'),
        ('Báo cáo / tạo file', 'Rpt/* (Pivot, Xsd, RptSchema…)\nQuickReport · QuickList · CatalogClone\nAiCatalog · FastCatalogRules\nBbxn (Word) · CashFlowDiagnostic\nReportBuilderModule'),
        ('Tích hợp', 'Api/* (LT3, Postman, DPAPI)\nFsgProjectLookup · FsgNotify (nvphyc)\nAiChat · WeeklyMail · MailTest\nLicense · ViewerControlServer\nSessionStore · AiHistoryStore'),
    ]
    for i, (t, b) in enumerate(svc):
        box(ax, 4 + i * 23.7, 64, 22.5, 11, t + '\n' + b, fs=7.3)

    ax.text(2, 79.2, 'Dữ liệu ngoài:  SQL Server (App/Sys, FSG_A)  ·  source UNC (+MobileService)  ·  %AppData%\\Bcode  ·  API Anthropic/Gemini/LT3  ·  SMTP/IMAP  ·  BcodeViewer / ScreenDesigner (Process.Start)',
            fontsize=8.2, color='#444', fontweight='bold')
    # arrows
    for x in (15, 38, 61, 85):
        arrow(ax, (x, 18), (x, 22), None, color='#555')
    arrow(ax, (50, 45), (50, 47), None, color='#555')
    arrow(ax, (50, 57), (50, 59), None, color='#555', both=True)
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
    box(ax, 48, 24, 48, 10.5, 'Host/ services:  ClaudeChatService (Anthropic + Gemini)  ·  SqlRunnerService (chạy SQL trong file, mặc định chặn ghi)\nSqlSchemaService + WorkspaceConnection (đọc chung settings.json của Bcode)  ·  WorkspaceSearchService (Find in Files)\nIncludeTree (đọc chuỗi include, link sang ScreenDesigner)  ·  CaptionTranslator + TranslateDictionary (v→e)\nMenuLauncher (F5)  ·  Settings/: LocalHistoryStore · SharedHistoryStore (lịch sử chung trong dự án)', fs=7.6)

    group(ax, 2, 38, 96, 27, 'Web/ (WebView2) — Monaco + JS thuần, nạp theo thứ tự trong index.html', 'web', fs=10)
    cols = [
        ('Khung & tiện ích', 'shell.js (menu/toolbar/cây file)\nui.js (hộp thoại theo theme)\nkeys.js (phím tắt tuỳ chỉnh)\nsettings.js · templates.js\ntabs.js · tabswitcher.js · quickopen.js\ntheme.js · dock.js · hostcall.js\nperfwatch.js (log độ mượt)'),
        ('Soạn thảo', 'editor.js (Monaco, tab, model,\nCtrl+Tab nhẹ)\nfcode-language.js (cú pháp FCode)\ncontextmenu.js (Goto File/Function,\nOpen Folder, Image File, Ctrl+I,\ndịch caption + Từ điển dịch, clone)\noutline.js · goto.js (Ctrl+G)\nhistory.js (lịch sử cục bộ + chung)'),
        ('IntelliSense & kiểm tra', 'completion.js (4 lớp gợi ý + AI ghost)\nentity.js (F12/Peek &Entity;)\nproblems.js + problems-worker.js\ncheckfield.js (Check Field:\nfields ↔ views, header)\nsearch.js (Find/Replace in Files)'),
        ('Xem trước & chạy', 'dirpreview.js + dirview.js (Alt+P Dir)\nmailpreview.js (Alt+P mail mẫu)\nsqlrun.js (Chạy SQL, Ctrl+Enter)\nchat.js (đã gỡ khỏi UI)'),
    ]
    for i, (t, b) in enumerate(cols):
        box(ax, 4 + i * 23.8, 43, 22.6, 20, t + '\n\n' + b, 'box', fs=7.8)

    arrow(ax, (40, 22), (48, 16), 'host.Begin*', both=True, color='#1976D2', loff=(0, -1.8))
    arrow(ax, (50, 36), (50, 38), None, both=True, color='#1976D2')
    ax.text(50, 67.3, 'Lưu trữ: %AppData%\\Bcode\\viewer-*.json (settings, hints, recent files, theme, translate-dict) · history\\ · viewer-perf.log · hint-cache · Config/*.txt (prompt AI cạnh exe)',
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
            ('Projects / Choose Server', 'ProjectPickerForm · NativeProjectPickerForm · ProjectActions (Ctrl+Shift+P / Ctrl+O → cửa sổ mới nếu đang có tab)'),
            ('Đồng bộ project FSG', 'Ctrl+F5 · FsgProjectLookupService (DB FSG_A) · FCodeConfigImportService'),
            ('Nhiều cửa sổ / khôi phục phiên', 'Ctrl+Shift+N · MainForm.Session (tab chờ) · Hibernate (chế độ hiệu năng)'),
            ('Backup DB · Create Menu · Open/Refresh Web.config', 'MainForm (menu Settings / Actions)'),
        ]),
        ('Khung giao diện', [
            ('Thanh trên / cột icon / thanh tool / tab', 'topbar.html / NativeTopBar · iconrail · _toolSpecs · FlatTabControl'),
            ('Quick Access + nhóm tool', 'QuickAccessForm · MainForm.ToolGroups (Alt+1..5)'),
            ('Command Palette · Tab switcher · phím tắt', 'CommandPaletteForm (Ctrl+P) · TabSwitcherForm · ShortcutRegistry (NativeDefault)'),
            ('Giao diện: theme / Template / tỉ lệ / hiệu năng', 'UiThemes · UiTemplate(+Form, tab Tìm file) · UiScale · UiOverrides · PerformanceProfile'),
            ('Trợ giúp (Ctrl+F1) · Thông báo y/c FSG (chuông)','HelpControl + docs/HuongDanSuDung.md · FsgNotifyService/Form'),
            ('Menu Triển khai · Tool mở rộng', 'DeployToolKeys / ExtToolKeys trong MainForm.cs'),
        ]),
        ('SQL & dữ liệu', [
            ('SQL Query (Monaco, AI, debug từng bước)', 'RawSqlControl · RawSqlService · SqlLineAnalyzer/SqlStepPlanner · sqleditor.html'),
            ('Write Schema (.xsd d1..dN) → cập nhật .rpt', 'Rpt/XsdSchemaWriter · RptSchemaForm · RptSchemaService (rptschema.ps1)'),
            ('Gợi ý code SQL', 'SqlHintCatalog/Service/Popular · sqlhints.js (App: SqlHintEngine/Jint · HintPopup)'),
            ('Lịch sử SQL & lịch sử sửa object', 'QueryHistoryControl · SqlHistoryForm · DdlTrackingService'),
            ('Table (menu của bảng, check partition, thiết kế cột)', 'TableEditControl · TableMenuService · PartitionCheckForm · ColumnDesignForm'),
            ('Command / Lookup (tìm chữ trong procedure)', 'SqlQueryControl · LookupControl · PeriodTableQueryService · SqlDefinitionIndex'),
            ('Kết quả: MultiResultView + menu lưới', 'resultview.html · ResultGridMenu · DataScriptService (Add Script)'),
            ('SQL Object tree / Ai đang dùng / So sánh object', 'SqlObjectTreeControl · UsagesControl · CompareObjectsControl'),
            ('Decrypt SQL Object (cần key)', 'DecryptSqlObjectControl · SqlDecryptor.Core.dll · LicenseService'),
            ('SQL Profiler · Change Owner · Compare Structure', 'SqlProfilerControl · ChangeOwnerForm · CompareStructureForm'),
        ]),
        ('Source FCode & menu ERP', [
            ('WCommand tree + CRUD + Gen Script Menu', 'WCommandTreeControl · WCommandService · WCommandEditForm · AppCommandEditForm'),
            ('Mobile (bảng dvcommand, MobileService)', 'WCommandTreeControl(menuTable) · AppCommandService.DvCommand · MobilePathService'),
            ('File Lookup (cache, preview, tìm, cấp nhanh)','FileLookupControl(+QuickGrant) · FileLookupService · FileParseCache · EntityCheck'),
            ('File Reference · Check Include', 'FileReferenceControl · IncludeCheckControl/Service (Assets/includecheck)'),
            ('Cấp source / View source / Copy to / Clone file', 'AddSourceForm · GrantSourceForm(SourceGrantService) · SourceViewForm · CopyMultiFileForm'),
            ('Gen Update (gói) · Note (New)/Advance Note', 'GenUpdatePackageControl · AdvanceNoteControl · GenAllService'),
            ('Tạo nhanh danh mục · Clone · AI tạo danh mục', 'QuickListForm/Service · CatalogClone · AiCatalogService · FastCatalogRules'),
        ]),
        ('Báo cáo & kế toán', [
            ('Tạo báo cáo (procedure có sẵn / thiết kế từ bảng)', 'ReportStudioForm · QuickReportControl · Bcode.ReportBuilder.dll'),
            ('Create RPT & XML · Excel → FRX', 'CreateRptControl · Services/Rpt/* · ExcelToFrx.dll (cần key)'),
            ('Check LCTT / CĐKT', 'CashFlowCheckControl · CashFlowDiagnosticService.* · CfsStandard/CfsFast'),
            ('Biên bản xác nhận (Word)', 'BbxnControl · BbxnService · BbxnForms'),
            ('Setup eInvoice (FE) · Check Mail · Mail kế hoạch tuần', 'SetupEInvoiceControl · CheckMailControl · WeeklyMailControl (Sent, phản hồi, Reply)'),
        ]),
        ('API & tích hợp', [
            ('Khai báo API (chuẩn LT3) · Tạo cấu trúc API', 'ApiDeclarationForm · Services/Api/* · ApiSchemaBuilderForm'),
            ('Thiết kế màn hình (exe riêng)', 'LaunchScreenDesigner → Bcode.ScreenDesigner.exe'),
            ('Yêu cầu FSG (QLYC) · FSG FBO (Ctrl+4, F12 DevTools)', 'FsgRequirementReportControl · QuickLaunchLoginForm · DevToolsDock'),
            ('Claude / Gemini (tab web nhúng)', 'AiWebPanel + Shared/AiWebHelper'),
            ('Mở BcodeViewer / nhận lệnh từ BcodeViewer', 'FileLookupControl (Process.Start) · ViewerControlServer (pipe)'),
        ]),
        ('Tiện ích', [
            ('Compare Text · String Beauty · Library · Note', 'CompareTextForm · StringBeautyControl · LibrarySnippetForm · NoteControl'),
            ('Script cart (Add/View/Clear/Save/Copy Script)', 'ScriptFileService · ScriptPopupForm'),
            ('Key bản quyền', 'LicenseKeyForm · LicenseService · Shared/LicenseCodec (Bcode.KeyGen cấp key)'),
        ]),
        ('Chế độ App (WinForms)', [
            ('Chọn chế độ · khung native', 'UiMode (--web/--native) · NativeShellBars · NativeProjectPickerForm'),
            ('Trang native thay trang HTML', 'NativePage + NativePages.Register · NativePages.*.cs (28 file)'),
            ('Bộ control dùng chung', 'NativeKit (NK.*, CardPanel, NK.Tip) · NativeInputs · PillButton · NativeSkin'),
            ('Tab native: SQL / Lookup / Table / Note…', 'NativeSqlControl (Scintilla, peek, AI, debug, Ctrl+S/F) · Native*Control'),
            ('Tô màu code · tìm trong editor', 'FcodeLexer · SqlScintilla (chặn ký tự điều khiển) · NativeFindBar'),
        ]),
    ]
    tree('Bcode.App — cây tính năng', bcode, 'dg_tree_bcode.png')

    viewer = [
        ('Soạn thảo lõi', [
            ('Monaco nhiều tab, mở/lưu, giữ encoding gốc', 'editor.js · tabs.js · BeginReadFile/BeginWriteFile'),
            ('Cú pháp FCode XML (JS / T-SQL / CSS lồng)', 'fcode-language.js'),
            ('Outline · Go to Definition · Ctrl+G tag', 'outline.js · goto.js'),
            ('Menu chuột phải FCode', 'contextmenu.js (Goto File/Function, Open Folder, Image File, Open Config, Clone, Dịch caption, Ctrl+I)'),
            ('Lịch sử cục bộ + lịch sử chung + diff', 'history.js · LocalHistoryStore · SharedHistoryStore'),
            ('Dịch caption v → e + Từ điển dịch', 'CaptionTranslator · TranslateDictionary (viewer-translate-dict.json)'),
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
            ('Check Field (fields ↔ views, header)', 'checkfield.js'),
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
            ('Clear Structure App · Open / Refresh Web.config', 'MainForm'),
            ('Log độ mượt (long task ≥ 100 ms)', 'perfwatch.js → viewer-perf.log (Help → Log độ mượt)'),
        ]),
    ]
    tree('BcodeViewer.App — cây tính năng', viewer, 'dg_tree_viewer.png')


if __name__ == '__main__':
    diag_overview(); diag_bcode(); diag_viewer(); diag_trees()
    print('done')
