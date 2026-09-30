using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>
/// Tra cứu / cào NGẦM (không hiện cửa sổ thật) dữ liệu dự án từ FSG:
///  - <see cref="LookupAsync"/>: tra 1 dự án CỤ THỂ (mở popup "Sửa dự án" của đúng dòng đó,
///    đọc đầy đủ Server/User/Pass, Database Sys/Reg, các đường dẫn, Host link...) — dùng khi
///    Bee bấm Ctrl+F5 ở MainForm, hoặc bấm "Thêm"/"Tìm dự án mới" ở ConnectionSettingsForm.
///  - <see cref="CrawlProjectListAsync"/>: cào NHANH toàn bộ danh sách dự án (chỉ các cột có
///    sẵn ngay trên bảng danh mục — Mã/Tên dự án, Server viết tắt, Database Sys, Mã phiên
///    bản, Bộ phận LT/TK, Tester — KHÔNG mở popup từng dòng vì sẽ quá chậm với nhiều dự án),
///    rồi lưu cache thành từng file JSON trong thư mục "configproject" cạnh Bcode.App.exe, để
///    ConnectionSettingsForm hiện sẵn một danh sách cho Bee chọn mà không cần cào lại mỗi lần.
///
/// Đăng nhập: dùng lại đúng cơ chế đăng nhập FastBusiness (LoginExtender_*) đã có ở
/// QuickLaunchLoginForm/FsgRequirementCrawlerForm — kể cả bước tự bấm "Có" khi gặp modal
/// "Tài khoản của bạn đang được đăng nhập và sử dụng" — và đọc CHUNG tài khoản đã lưu ở
/// "FSG Yêu cầu" (fsg_crawler_config.json).
///
/// ĐÃ SỬA (lần cào/tra thử đầu bị lỗi khác nhau mỗi lần — "không thấy khung đăng nhập" rồi
/// lần khác lại "không mở được hộp lọc" — dù dự án BVNGOCPHU chắc chắn có thật):
///  1. WebView2 ẩn dùng chung profile/cookie mặc định với cửa sổ "FSG Yêu cầu" (đang mở, đã
///     đăng nhập) trong cùng 1 tiến trình Bcode.App.exe. Khi cửa sổ ẩn này Navigate tới
///     Login.aspx, cookie phiên cũ vẫn còn hiệu lực nên trang có thể tự nhảy thẳng vào màn
///     hình chính (không hiện lại form đăng nhập) — khớp với lỗi "Không tìm thấy khung đăng
///     nhập chuẩn". Sửa: cửa sổ ẩn giờ dùng 1 UserDataFolder RIÊNG (thư mục
///     "fsg_lookup_webview2_profile" cạnh .exe) — độc lập cookie với "FSG Yêu cầu", luôn đăng
///     nhập lại từ đầu bằng chính username/password đã lưu. Đồng thời vẫn giữ 1 lớp dự phòng:
///     nếu vẫn gặp NO_USER mà URL hiện tại KHÔNG còn là Login.aspx (tức đã tự chuyển trang vì
///     lý do khác), coi như đã đăng nhập sẵn và dùng luôn, thay vì báo lỗi.
///  2. Cửa sổ host trước đây không bao giờ gọi Show() (chỉ ép tạo handle) — vài máy/driver
///     WebView2 có thể không init/tải trang ổn định khi control chưa từng "hiện" 1 lần. Giờ
///     gọi host.Show() nhưng đặt Opacity = 0 + kích thước 1x1 + toạ độ ngoài màn hình, nên vẫn
///     hoàn toàn vô hình với Bee.
///  3. Bước mở hộp lọc & bấm nút Tìm kiếm trước đây chỉ đợi 800ms cố định rồi bấm 1 lần duy
///     nhất — nếu trang Danh mục dự án tải chậm hơn bình thường thì nút bấm "hụt" và cả vòng
///     lặp chờ (4.5s) trôi qua vô ích. Giờ: chờ nút Tìm kiếm THỰC SỰ xuất hiện trong DOM trước,
///     rồi bấm LẠI ở mỗi vòng lặp (không chỉ 1 lần) cho tới khi hộp lọc hiện ra.
///  4. Các thông báo lỗi giờ kèm theo URL trang đang đứng — nếu vẫn còn lỗi thì Bee chụp màn
///     hình thông báo mới sẽ có thêm manh mối debug thay vì chỉ 1 câu chung chung.
///  5. (Bee xác nhận qua video) "Sửa dự án" KHÔNG mở bằng double-click — phải CHỌN dòng rồi
///     bấm nút "Sửa" (gọi executeCommand Edit). Lần sửa đầu chọn dòng bằng cách dispatch
///     'click' lên thẻ &lt;tr&gt; — xem kỹ video mới phát hiện thẻ &lt;tr&gt; không có onclick
///     nào cả, việc chọn dòng thật ra chạy qua onfocus="_focus(this)" gắn trên TỪNG Ô INPUT
///     của dòng — nên trước đó control không hề nhận ra dòng đã được chọn. Đã sửa: gọi
///     .focus() thẳng vào input Mã dự án của dòng, đúng cơ chế chọn dòng thật.
///  6. (Bee xác nhận) trang Danh mục dự án có RẤT nhiều trang (Bee gửi pager: 339 trang / 3384
///     dự án ở cỡ trang mặc định 10) và phải lật qua hết mới đủ dữ liệu — CrawlProjectListAsync
///     giờ tự lật hết các trang bằng goToPage() và gộp lại, không chỉ trông cậy vào
///     set_gridPageSize (UI chỉ cho chọn 5/10/15/20/25 nên set thẳng số lớn hơn có thể bị bỏ
///     qua). Vì phải lật nhiều trang nên việc cào lần đầu có thể mất vài phút — đã báo trước
///     cho Bee trong dòng trạng thái ở ConnectionSettingsForm.
///  7. TÌM RA NGUYÊN NHÂN GỐC của lỗi "không mở được hộp thoại lọc": trang Danh mục dự án
///     (nbdmda.aspx) KHÔNG dùng hộp thoại lọc dạng modal (searchExtender) như trang Yêu cầu —
///     đó là lý do 2 lần sửa trước đều không ăn thua dù đã thử nhiều cách bấm/chờ khác nhau,
///     vì đang nhắm vào 1 phần tử hoàn toàn không tồn tại trên trang này. HTML thật Bee gửi
///     (đang ở trạng thái đã lọc theo "BVNGOCPHU") cho thấy trang này lọc nhanh ngay tại ô nhập
///     dưới mỗi cột (input id="..._FilterPanelTextma_da", gõ xong bấm Enter thì hàm
///     onkeypress R0(this,event,0) áp dụng lọc) — không có popup/dialog nào cả. Đã viết lại
///     hoàn toàn bước lọc trong LookupAsync theo đúng cơ chế này (gõ giá trị vào ô đó rồi gọi
///     thẳng hàm R0 với event giả lập keyCode=13).
///  8. Sau bản sửa (5) vẫn KHÔNG mở được popup Sửa — chẩn đoán lần này cho thấy popup còn
///     không được TẠO RA trong DOM (không có id nào chứa "dirExtender"), dù executeCommand
///     ('Edit') chạy không lỗi. Nghi vấn: cửa sổ WebView2 ẩn nằm NGOÀI màn hình và trước giờ
///     chưa bao giờ được activate ở mức OS — Chromium có thể coi document này "chưa từng có
///     focus" nên mọi thao tác cần trạng thái focus/chọn dòng đều không có tác dụng thật, dù
///     gọi đúng hàm JS. Đã thêm host.Activate() + web.Focus() ngay sau khi tạo cửa sổ (và gọi
///     lại trước khi chọn dòng) — cửa sổ vẫn nằm ngoài màn hình + Opacity=0 nên việc activate
///     không hiện gì với Bee, chỉ có khả năng làm giật focus bàn phím/chuột của Bee trong tích
///     tắc nếu đúng lúc đó Bee đang gõ ở cửa sổ khác — Bee để ý xem có gặp hiện tượng này
///     không, nếu có sẽ cần đổi hướng khác. Đồng thời giả lập thêm 'mouseover' trên dòng
///     (control có onmouseover gọi _highlightItem — rất có thể đây mới là cách đánh dấu "dòng
///     hiện hành" thật, không chỉ riêng .focus() trên ô input) và đủ chuỗi
///     mousedown/focus/mouseup/click trên ô, để tăng khả năng khớp đúng cơ chế chọn dòng nội
///     bộ của control dù không có mã nguồn JS gốc để đọc. (Bản sửa này đã CHẠY ĐƯỢC — Bee xác
///     nhận popup "Sửa dự án" mở và điền được vào Workspace của Bcode.)
///  9. Sau khi (8) chạy được, Bee báo popup "Edit Project" (của chính Bcode, KHÔNG phải FSG)
///     mở lên NGAY SAU ĐÓ bị mất hết nút Test Connection/Lưu/Hủy. Popup đó dùng
///     WebActionBar — 1 control cũng render bằng WebView2 riêng (môi trường MẶC ĐỊNH của
///     app, khác hẳn profile riêng "fsg_lookup_webview2_profile" mà lookup dùng). Nghi vấn:
///     tiến trình WebView2 ẩn (lookup) vừa Dispose() còn chưa thoát hẳn ở tầng OS thì
///     EditProjectForm đã mở ngay, 2 tiến trình WebView2 chồng lấn nhau khiến WebActionBar
///     không init/navigate xong kịp. Đã sửa: sau khi Dispose() cửa sổ ẩn, chờ thêm 500ms rồi
///     mới trả kết quả về cho MainForm/ConnectionSettingsForm — nhường đủ thời gian cho tiến
///     trình cũ thoát hẳn trước khi mở dialog mới có WebView2 riêng.
///
/// LƯU Ý CÒN LẠI (nhắc lại với Bee ở phần giải thích, không chỉ ở đây):
///  - Vài trường ánh xạ sang Workspace trong LookupAsync là suy luận tốt nhất từ 1 ví dụ dự án
///    (1ON1) Bee gửi: "addreg" (dạng "Database:XXX") -> AppDatabase, "Host (link) 2" ->
///    LoginWLink. Bee nên rà lại trong popup Edit Project trước khi lưu. XÁC NHẬN: với dự án
///    BVNGOCPHU, "addreg" không theo đúng dạng "Database:XXX" nên AppDatabase bị để trống —
///    CHƯA rõ trường nào trong popup "Sửa dự án" (nếu có) mới thật sự là Database App cho dự
///    án này; cần Bee xem lại đúng field đó trong popup FSG rồi cho biết field id, để sửa
///    mapping cho đúng thay vì đoán tiếp qua "addreg".
///  - CrawlProjectListAsync giờ lật hết các trang rồi gộp lại — cào xong Bee nên xác nhận số
///    dự án cào được (hiện trong dòng trạng thái) có khớp con số thật trên FSG (Bee gửi trước
///    đó là 3384) không, phòng trường hợp vòng lặp dừng sớm vì lý do nào đó.
/// </summary>
public sealed class FsgProjectLookupService
{
    private const string LoginUrl = "http://172.168.5.14:81/FSG/Main/Login.aspx";
    private const string ListUrl = "http://172.168.5.14:81/FSG/Main/nbdmda.aspx?id=05.20.10";
    private static readonly string CrawlerConfigPath = Path.Combine(AppContext.BaseDirectory, "fsg_crawler_config.json");

    /// <summary>Thư mục cache các dự án đã cào nhanh (1 file .json / dự án) — cạnh Bcode.App.exe.</summary>
    public static string CacheFolder => Path.Combine(AppContext.BaseDirectory, "configproject");

    /// <summary>UserDataFolder RIÊNG cho WebView2 ẩn dùng để tra/cào FSG — tách khỏi profile
    /// mặc định mà "FSG Yêu cầu"/"FSG dự án" đang dùng, để 2 bên không đụng cookie/session của
    /// nhau (xem giải thích ở phần "ĐÃ SỬA" trên đầu file).</summary>
    private static string LookupWebViewProfileFolder => Path.Combine(AppContext.BaseDirectory, "fsg_lookup_webview2_profile");

    public sealed class Result
    {
        public bool Found;
        public string? Error;
        public Workspace? Workspace;

        /// <summary>Các trường tra được nhưng KHÔNG có chỗ tương ứng trong Workspace (Tên dự
        /// án, Mã phiên bản, Bộ phận triển khai/lập trình, Tester) — chỉ để hiện cho Bee xem
        /// qua, không lưu vào đâu cả.</summary>
        public string? Summary;
    }

    public sealed class CrawlResult
    {
        public bool Success;
        public string? Error;
        public List<FsgProjectSummary> Projects { get; set; } = new();
    }

    /// <summary>Bản ghi rút gọn 1 dự án lấy trực tiếp từ bảng danh mục (không mở popup từng
    /// dòng) — đủ để Bee tìm/nhận diện dự án trong danh sách cache, KHÔNG có User/Pass/đường
    /// dẫn (những cái đó chỉ có khi tra chi tiết qua <see cref="LookupAsync"/>).</summary>
    public sealed class FsgProjectSummary
    {
        public string MaDuAn { get; set; } = "";
        public string TenDuAn { get; set; } = "";
        public string Server { get; set; } = "";
        public string DbSys { get; set; } = "";
        public string MaPhienBan { get; set; } = "";
        public string BoPhanLapTrinh { get; set; } = "";
        public string BoPhanTrienKhai { get; set; } = "";
        public string Tester { get; set; } = "";

        public override string ToString() =>
            string.IsNullOrWhiteSpace(TenDuAn) ? MaDuAn : $"{MaDuAn} — {TenDuAn}";
    }

    // Modal "Tài khoản của bạn đang được đăng nhập và sử dụng. Bạn có muốn đóng không?" — y hệt
    // script đã dùng ở QuickLaunchLoginForm/FsgRequirementCrawlerForm.
    private const string AlreadyLoggedInConfirmScript = @"
    (function() {
        var label = document.querySelector('.ModalBackgroundChildFormLabel');
        if (!label) return 'NO_MODAL';
        var text = label.textContent || '';
        if (text.indexOf('đang được đăng nhập') === -1) return 'NO_MODAL';

        var modal = label.closest('.ModalBackgroundChild') || document.querySelector('.ModalBackgroundChild');
        if (!modal) return 'NO_MODAL';

        var buttons = modal.querySelectorAll('button');
        var btnYes = null;
        for (var i = 0; i < buttons.length; i++) {
            var onclickAttr = buttons[i].getAttribute('onclick') || '';
            if (onclickAttr.indexOf('_login(true)') !== -1) { btnYes = buttons[i]; break; }
        }
        if (!btnYes) return 'NO_BUTTON';

        btnYes.click();
        return 'CONFIRMED';
    })();";

    /// <summary>Tra CHI TIẾT 1 dự án (mở popup "Sửa dự án" để lấy Server/User/Pass thật, các
    /// đường dẫn, Host link...) — dùng cho Ctrl+F5 ở MainForm và nút Thêm/Tìm dự án mới ở
    /// ConnectionSettingsForm.</summary>
    public async Task<Result> LookupAsync(string projectCode)
    {
        var (host, web, err) = await OpenLoggedInFsgAsync();
        if (err is not null || host is null || web is null)
            return new Result { Found = false, Error = err };

        try
        {
            return await LookupCoreAsync(host, web, projectCode);
        }
        finally
        {
            host.Dispose();
            // Nhường 1 nhịp cho tiến trình WebView2 ẩn vừa Dispose thật sự thoát hẳn, TRƯỚC
            // khi trả kết quả về cho bên gọi — MainForm/ConnectionSettingsForm thường mở ngay
            // EditProjectForm sau đây, mà EditProjectForm lại có thanh nút riêng
            // (WebActionBar) CŨNG dùng WebView2 (môi trường mặc định của app, khác profile
            // riêng của lookup). Nếu 2 tiến trình WebView2 còn chồng lấn nhau đúng lúc
            // EditProjectForm khởi tạo, thanh nút đó có thể không dựng được — khớp với việc
            // Bee gặp mất cả nút Test Connection lẫn Lưu/Hủy ngay sau khi tra cứu FSG chạy
            // xong.
            await Task.Delay(500);
        }
    }

    private async Task<Result> LookupCoreAsync(Form host, WebView2 web, string projectCode)
    {
            try
            {
                // ---- Mở trang Danh mục dự án — chờ trang THỰC SỰ dựng xong. ----
                web.CoreWebView2!.Navigate(ListUrl);
                await WaitForPageLoadAsync(web);

                bool pageReady = false;
                for (int i = 0; i < 15; i++)
                {
                    var check = await web.ExecuteScriptAsync(@"
                    (function() {
                        return !!(window.$find && window.$find('ctl00_FastBusiness_MainReport')
                            && document.getElementById('ctl00_FastBusiness_MainReport_FilterPanelTextma_da'));
                    })();");
                    if (check == "true") { pageReady = true; break; }
                    await Task.Delay(300);
                }
                if (!pageReady)
                {
                    var currentUrl = web.CoreWebView2!.Source;
                    return new Result { Found = false, Error = $"Trang Danh mục dự án FSG không tải/dựng xong (URL hiện tại: {currentUrl})." };
                }

                // ---- Lọc theo Mã dự án — trang này KHÔNG dùng hộp thoại lọc dạng modal như
                // trang Yêu cầu, mà lọc nhanh ngay tại ô nhập dưới cột "Mã dự án" (xác nhận qua
                // HTML thật Bee gửi: input FilterPanelTextma_da, gõ xong bấm Enter thì hàm
                // onkeypress R0(this,event,0) áp dụng lọc). Gọi thẳng R0 với 1 object giả lập
                // sự kiện keyCode=13 thay vì dispatch KeyboardEvent thật — keyCode trên
                // KeyboardEvent chuẩn là read-only nên trình duyệt Chromium (nền của WebView2)
                // có thể bỏ qua giá trị truyền vào constructor, gọi thẳng hàm là chắc ăn nhất.
                var codeEscaped = JsEscape(projectCode.Trim());
                var applyFilterScript = $@"
                (function() {{
                    var input = document.getElementById('ctl00_FastBusiness_MainReport_FilterPanelTextma_da');
                    if (!input) return 'NO_FILTER_FIELD';
                    input.value = '{codeEscaped}';
                    var rpt = window.$find ? window.$find('ctl00_FastBusiness_MainReport') : null;
                    if (rpt && typeof rpt.R0 === 'function') {{
                        rpt.R0(input, {{ keyCode: 13, which: 13, preventDefault: function(){{}}, stopPropagation: function(){{}} }}, 0);
                        return 'FILTERED';
                    }}
                    return 'NO_R0';
                }})();";
                var applyResult = (await web.ExecuteScriptAsync(applyFilterScript)).Trim('"');
                if (applyResult == "NO_FILTER_FIELD")
                    return new Result { Found = false, Error = "Không tìm thấy ô lọc nhanh \"Mã dự án\" trên trang Danh mục dự án (giao diện FSG có thể đã đổi)." };
                if (applyResult == "NO_R0")
                    return new Result { Found = false, Error = "Không gọi được hàm áp dụng lọc (R0) trên trang Danh mục dự án." };

                // ---- Chờ lưới lọc xong đúng dự án (postback qua UpdatePanel, không có sự
                // kiện NavigationCompleted để chờ) và xác nhận dòng đầu khớp mã. ----
                var codeUpper = projectCode.Trim().ToUpperInvariant().Replace("'", "\\'");
                bool matched = false;
                for (int i = 0; i < 15; i++)
                {
                    await Task.Delay(400);
                    var matchCheckScript = $@"
                    (function() {{
                        var input = document.getElementById('ctl00_FastBusiness_MainReport_inputCell_1.1');
                        if (!input) return 'NOT_FOUND';
                        return input.value.trim().toUpperCase() === '{codeUpper}' ? 'MATCH' : 'NOT_FOUND';
                    }})();";
                    var matchResult = await web.ExecuteScriptAsync(matchCheckScript);
                    if (matchResult.Trim('"') == "MATCH") { matched = true; break; }
                }
                if (!matched)
                    return new Result { Found = false, Error = $"Không thấy dự án \"{projectCode}\" trong Danh mục dự án FSG (đã lọc nhưng dòng đầu không khớp)." };

                // ---- Mở popup "Sửa dự án": CHỌN dòng đầu rồi bấm nút "Sửa" trên toolbar —
                // đúng theo video Bee gửi. Lần sửa trước chỉ gọi .focus() vào ô input Mã dự án
                // (vì <tr> không có onclick, chọn dòng chạy qua onfocus="_focus(this)" của
                // input) — Chẩn đoán lần chạy sau cho thấy popup Sửa vẫn KHÔNG hề được tạo ra
                // trong DOM (không có id nào chứa "dirExtender"), dù executeCommand('Edit') đã
                // chạy không lỗi. Giờ vá thêm 2 hướng cùng lúc:
                //  a) Dòng lưới còn có onmouseover gọi _highlightItem(this,0) — rất có thể đó
                //     mới là cách control đánh dấu "dòng hiện hành" cho lệnh Edit, không phải
                //     riêng .focus(). Nên giả lập thêm 'mouseover' trên <tr> (không chỉ focus
                //     ô input) trước khi gọi Edit.
                //  b) Cửa sổ WebView2 ẩn nằm NGOÀI màn hình + chưa từng được activate ở mức OS
                //     — Chromium có thể coi document là "chưa từng có focus" nên các thao tác
                //     cần trạng thái focus/selection không có tác dụng dù gọi đúng hàm JS. Đã
                //     thêm host.Activate() + web.Focus() ngay sau khi tạo cửa sổ (xem
                //     OpenLoggedInFsgAsync) — gọi lại 1 lần nữa ở đây cho chắc, phòng khi
                //     postback/điều hướng qua nhiều trang làm mất trạng thái đó.
                web.Focus();
                await web.ExecuteScriptAsync(@"
                (function() {
                    var row = document.getElementById('ctl00_FastBusiness_MainReport_gridRow1');
                    if (row) row.dispatchEvent(new MouseEvent('mouseover', { bubbles: true, cancelable: true }));
                    var cell = document.getElementById('ctl00_FastBusiness_MainReport_inputCell_1.1');
                    if (cell) {
                        cell.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
                        cell.focus();
                        cell.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
                        cell.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                    }
                })();");
                await Task.Delay(400);
                await web.ExecuteScriptAsync(@"
                (function() {
                    var rpt = window.$find ? window.$find('ctl00_FastBusiness_MainReport') : null;
                    if (rpt && typeof rpt.executeCommand === 'function') {
                        rpt.executeCommand({ commandName: 'Edit', commandArgument: '0' });
                        return 'EDIT_COMMAND';
                    }
                    var btn = document.getElementById('ctl00_FastBusiness_MainReport_ToolbarButton_Edit');
                    if (btn) { btn.click(); return 'EDIT_BTN_CLICKED'; }
                    return 'NO_EDIT_TRIGGER';
                })();");

                bool editOpen = false;
                for (int i = 0; i < 15; i++)
                {
                    await Task.Delay(300);
                    var check = await web.ExecuteScriptAsync(@"
                    (function() {
                        var f = document.getElementById('ctl00_FastBusiness_MainReport_dirExtender_form_ma_da');
                        return !!(f && f.offsetParent !== null);
                    })();");
                    if (check == "true") { editOpen = true; break; }
                }
                if (!editOpen)
                {
                    // Vẫn chẩn đoán thêm 1 lớp nữa phòng khi cách chọn dòng qua .focus() vẫn
                    // chưa đúng hẳn — liệt kê các id có "dirExtender"/"Edit" để có đầu mối ngay
                    // nếu còn lỗi, khỏi phải quay video thêm lần nữa.
                    var diag = await web.ExecuteScriptAsync(@"
                    (function() {
                        var ids = [];
                        var all = document.querySelectorAll('[id]');
                        for (var i = 0; i < all.length; i++) {
                            var id = all[i].id;
                            if (/dirExtender|Extender|Edit|Sua|Popup|Modal/i.test(id)) ids.push(id);
                        }
                        return { ids: ids.slice(0, 40) };
                    })();");
                    return new Result { Found = false, Error = $"Không mở được popup \"Sửa dự án\" (chọn dòng + bấm nút Sửa không có tác dụng — giao diện FSG có thể khác so với suy đoán).\nChẩn đoán: {diag}" };
                }

                // ---- Đọc dữ liệu trong popup ----
                var readScript = @"
                (function() {
                    function v(id) { var e = document.getElementById(id); return e ? (e.value || '') : ''; }
                    return {
                        ma_da: v('ctl00_FastBusiness_MainReport_dirExtender_form_ma_da'),
                        ten_da: v('ctl00_FastBusiness_MainReport_dirExtender_form_ten_da'),
                        ma_pbsp: v('ctl00_FastBusiness_MainReport_dirExtender_form_ma_pbsp'),
                        bp_tk: v('ctl00_FastBusiness_MainReport_dirExtender_form_bp_tk'),
                        bp_lt: v('ctl00_FastBusiness_MainReport_dirExtender_form_bp_lt'),
                        xuser: v('ctl00_FastBusiness_MainReport_dirExtender_form_xuser'),
                        xpass: v('ctl00_FastBusiness_MainReport_dirExtender_form_xpass'),
                        db_sys: v('ctl00_FastBusiness_MainReport_dirExtender_form_db_sys'),
                        ten_server: v('ctl00_FastBusiness_MainReport_dirExtender_form_ten_server'),
                        addreg: v('ctl00_FastBusiness_MainReport_dirExtender_form_addreg'),
                        dir_pro_web: v('ctl00_FastBusiness_MainReport_dirExtender_form_dir_pro_web'),
                        dir_src_web: v('ctl00_FastBusiness_MainReport_dirExtender_form_dir_src_web'),
                        dir_update: v('ctl00_FastBusiness_MainReport_dirExtender_form_dir_update'),
                        web_host1: v('ctl00_FastBusiness_MainReport_dirExtender_form_web_host1'),
                        web_host2: v('ctl00_FastBusiness_MainReport_dirExtender_form_web_host2'),
                        ma_tester1: v('ctl00_FastBusiness_MainReport_dirExtender_form_ma_tester1')
                    };
                })();";
                // ExecuteScriptAsync tự JSON-hoá giá trị trả về của JS (object ở đây) thành 1
                // chuỗi JSON — KHÔNG cần JSON.stringify thủ công trong script.
                var json = await web.ExecuteScriptAsync(readScript);

                // ---- Đóng popup KHÔNG lưu (bấm "Hủy") ----
                await web.ExecuteScriptAsync(@"
                (function() {
                    var btn = document.getElementById('ctl00_FastBusiness_MainReport_dirExtender_updateDlgCancel');
                    if (btn) btn.click();
                })();");

                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                string Get(string k) => data.TryGetValue(k, out var val) ? (val ?? "") : "";

                var server = Get("ten_server");
                var semi = server.IndexOf(';');
                if (semi >= 0) server = server[..semi];
                server = server.Trim();

                var addreg = Get("addreg").Trim();
                var appDb = addreg.StartsWith("Database:", StringComparison.OrdinalIgnoreCase)
                    ? addreg["Database:".Length..].Trim()
                    : "";

                var loginLink = Get("web_host2");
                if (string.IsNullOrWhiteSpace(loginLink)) loginLink = Get("web_host1");

                var ws = new Workspace
                {
                    Name = Get("ma_da"),
                    Server = server,
                    IntegratedSecurity = false,
                    User = Get("xuser"),
                    Password = Get("xpass"),
                    SysDatabase = Get("db_sys"),
                    AppDatabase = appDb,
                    ProjectId = Get("ma_da"),
                    LoginWLink = loginLink,
                    ProgramPath = Get("dir_pro_web"),
                    SourcePath = Get("dir_src_web"),
                    MobilePath = "",
                    WorkingPath = Get("dir_update"),
                    RegistryName = "Software\\Fast",
                };

                var summaryParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Get("ten_da"))) summaryParts.Add($"Tên dự án: {Get("ten_da")}");
                if (!string.IsNullOrWhiteSpace(Get("ma_pbsp"))) summaryParts.Add($"Mã phiên bản: {Get("ma_pbsp")}");
                if (!string.IsNullOrWhiteSpace(Get("bp_tk"))) summaryParts.Add($"BP triển khai: {Get("bp_tk")}");
                if (!string.IsNullOrWhiteSpace(Get("bp_lt"))) summaryParts.Add($"BP lập trình: {Get("bp_lt")}");
                if (!string.IsNullOrWhiteSpace(Get("ma_tester1"))) summaryParts.Add($"Tester: {Get("ma_tester1")}");

                return new Result
                {
                    Found = true,
                    Workspace = ws,
                    Summary = summaryParts.Count > 0 ? string.Join("  |  ", summaryParts) : null
                };
            }
            catch (Exception ex)
            {
                return new Result { Found = false, Error = "Lỗi tra cứu FSG: " + ex.Message };
            }
    }

    /// <summary>Cào NHANH toàn bộ danh mục dự án (chỉ các cột có sẵn trên bảng danh sách,
    /// không mở popup từng dòng) và lưu cache ra <see cref="CacheFolder"/> — mỗi dự án 1 file
    /// .json, tên file = mã dự án.</summary>
    public async Task<CrawlResult> CrawlProjectListAsync()
    {
        var (host, web, err) = await OpenLoggedInFsgAsync();
        if (err is not null || host is null || web is null)
            return new CrawlResult { Success = false, Error = err };

        try
        {
            return await CrawlProjectListCoreAsync(host, web);
        }
        finally
        {
            host.Dispose();
            // Cùng lý do đã ghi ở LookupAsync — nhường 1 nhịp cho tiến trình WebView2 ẩn thoát
            // hẳn trước khi trả kết quả, tránh chồng lấn với WebView2 của các dialog khác mở
            // ngay sau đó.
            await Task.Delay(500);
        }
    }

    private async Task<CrawlResult> CrawlProjectListCoreAsync(Form host, WebView2 web)
    {
            try
            {
                web.CoreWebView2!.Navigate(ListUrl);
                await WaitForPageLoadAsync(web);

                // Chờ trang thực sự dựng xong (report control sẵn sàng) trước khi đổi page
                // size — tránh gọi set_gridPageSize khi control JS chưa kịp khởi tạo.
                bool pageReady = false;
                for (int i = 0; i < 15; i++)
                {
                    var check = await web.ExecuteScriptAsync(@"
                    (function() {
                        return !!(window.$find && window.$find('ctl00_FastBusiness_MainReport'));
                    })();");
                    if (check == "true") { pageReady = true; break; }
                    await Task.Delay(300);
                }
                if (!pageReady)
                {
                    var currentUrl = web.CoreWebView2!.Source;
                    return new CrawlResult { Success = false, Error = $"Trang Danh mục dự án FSG không tải/dựng xong (URL hiện tại: {currentUrl})." };
                }

                // Thử nâng số dòng/trang lên (tối ưu, không bắt buộc phải thành công) — nếu
                // FSG chấp nhận thì sẽ ít trang hơn hẳn, vòng lặp lật trang bên dưới sẽ dừng
                // sớm. Nhưng theo Bee mô tả, trang Danh mục dự án có RẤT nhiều trang (Bee gửi
                // pager cho thấy 339 trang / 3384 dự án ở cỡ trang mặc định 10) và ô chọn cỡ
                // trang trên UI chỉ có 5/10/15/20/25 — rất có thể set_gridPageSize(3000) bị
                // server bỏ qua/chặn, nên KHÔNG được coi đây là đủ: phải tự lật qua toàn bộ
                // các trang bằng goToPage() và gộp dữ liệu lại, đúng như Bee yêu cầu.
                await web.ExecuteScriptAsync(@"
                (function() {
                    var rpt = window.$find ? window.$find('ctl00_FastBusiness_MainReport') : null;
                    if (rpt && typeof rpt.set_gridPageSize === 'function') { rpt.set_gridPageSize(200); }
                })();");
                await Task.Delay(1200);

                const string readPageRowsScript = @"
                (function() {
                    function v(row, col) {
                        var e = document.getElementById('ctl00_FastBusiness_MainReport_inputCell_' + row + '.' + col);
                        return e ? (e.value || '') : '';
                    }
                    var rows = [];
                    for (var i = 1; i <= 5000; i++) {
                        if (!document.getElementById('ctl00_FastBusiness_MainReport_inputCell_' + i + '.1')) break;
                        rows.push({
                            MaDuAn: v(i, 1), TenDuAn: v(i, 2), Server: v(i, 3), DbSys: v(i, 4),
                            MaPhienBan: v(i, 5), Tester: v(i, 8), BoPhanLapTrinh: v(i, 9), BoPhanTrienKhai: v(i, 10)
                        });
                    }
                    return rows;
                })();";

                const string hasNextPageScript = @"
                (function() {
                    var links = document.querySelectorAll('.GridPager a.PaddedLink');
                    for (var i = 0; i < links.length; i++) {
                        if (links[i].textContent.indexOf('Tiếp') !== -1) return true;
                    }
                    return false;
                })();";

                var allProjects = new List<FsgProjectSummary>();
                string? lastFirstCode = null;

                // Chỉ số trang goToPage() bắt đầu từ 0 (trang hiện tại lúc mới vào là trang 0
                // — không cần gọi goToPage cho trang đầu). Trần 400 trang để không lặp vô hạn
                // nếu giao diện đổi khác đi (Bee cho biết thực tế có 339 trang).
                for (int page = 0; page < 400; page++)
                {
                    if (page > 0)
                    {
                        await web.ExecuteScriptAsync($@"
                        (function() {{
                            var rpt = window.$find ? window.$find('ctl00_FastBusiness_MainReport') : null;
                            if (rpt && typeof rpt.goToPage === 'function') {{ rpt.goToPage({page}); }}
                        }})();");
                        // Trang mới load qua UpdatePanel (không phải NavigationCompleted) nên
                        // phải chờ 1 khoảng cố định thay vì WaitForPageLoadAsync.
                        await Task.Delay(700);
                    }

                    var json = await web.ExecuteScriptAsync(readPageRowsScript);
                    var rows = JsonSerializer.Deserialize<List<FsgProjectSummary>>(json) ?? new();
                    rows.RemoveAll(p => string.IsNullOrWhiteSpace(p.MaDuAn));
                    if (rows.Count == 0) break;

                    // goToPage(page) có thể không còn tác dụng nếu đã hết trang thật (id quá
                    // lớn) — nếu dòng đầu trùng y hệt lần đọc trước thì coi như đã lặp lại,
                    // dừng lại thay vì gộp trùng dữ liệu.
                    if (lastFirstCode is not null && rows[0].MaDuAn == lastFirstCode) break;
                    lastFirstCode = rows[0].MaDuAn;

                    allProjects.AddRange(rows);

                    var hasNext = await web.ExecuteScriptAsync(hasNextPageScript);
                    if (hasNext != "true") break;
                }

                // Cùng 1 dự án có thể lặp giữa các trang nếu cỡ trang thực tế lớn hơn 10 (do
                // set_gridPageSize(200) phía trên có thể đã có hiệu lực một phần) — loại trùng
                // theo Mã dự án, giữ bản ghi đọc được đầu tiên.
                var projects = allProjects
                    .GroupBy(p => p.MaDuAn, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                SaveCache(projects);
                return new CrawlResult { Success = true, Projects = projects };
            }
            catch (Exception ex)
            {
                return new CrawlResult { Success = false, Error = "Lỗi cào danh sách dự án FSG: " + ex.Message };
            }
    }

    /// <summary>Ghi cache — mỗi dự án 1 file .json trong <see cref="CacheFolder"/>. Lỗi ghi
    /// đĩa (ổ đầy, không có quyền...) chỉ bỏ qua, không được làm gãy luồng cào.</summary>
    public static void SaveCache(List<FsgProjectSummary> projects)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            foreach (var p in projects)
            {
                if (string.IsNullOrWhiteSpace(p.MaDuAn)) continue;
                var invalid = Path.GetInvalidFileNameChars();
                var safeName = new string(p.MaDuAn.Where(c => !invalid.Contains(c)).ToArray());
                if (safeName.Length == 0) continue;
                var path = Path.Combine(CacheFolder, safeName + ".json");
                File.WriteAllText(path, JsonSerializer.Serialize(p));
            }
        }
        catch { /* cache là tiện ích thêm, lỗi ghi đĩa không được làm gãy luồng cào */ }
    }

    /// <summary>Đọc cache đã lưu (không đụng mạng) — dùng để hiện danh sách trong
    /// ConnectionSettingsForm ngay khi mở form, khỏi phải cào lại mỗi lần.</summary>
    public static List<FsgProjectSummary> LoadCache()
    {
        var result = new List<FsgProjectSummary>();
        try
        {
            if (!Directory.Exists(CacheFolder)) return result;
            foreach (var file in Directory.GetFiles(CacheFolder, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var p = JsonSerializer.Deserialize<FsgProjectSummary>(json);
                    if (p is not null && !string.IsNullOrWhiteSpace(p.MaDuAn)) result.Add(p);
                }
                catch { /* 1 file cache lỗi không được chặn các file còn lại */ }
            }
        }
        catch { }
        return result.OrderBy(p => p.MaDuAn, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (string user, string pass) ReadSharedFsgLogin()
    {
        try
        {
            if (!File.Exists(CrawlerConfigPath)) return ("", "");
            var json = File.ReadAllText(CrawlerConfigPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var user = root.TryGetProperty("User", out var u) ? (u.GetString() ?? "") : "";
            var pass = root.TryGetProperty("Pass", out var p) ? (p.GetString() ?? "") : "";
            return (user, pass);
        }
        catch { return ("", ""); }
    }

    /// <summary>Tạo 1 WebView2 ẩn (profile RIÊNG, không đụng cookie với "FSG Yêu cầu"/"FSG dự
    /// án" đang mở) và đăng nhập FSG bằng tài khoản chung đã lưu — dùng chung cho cả
    /// LookupAsync và CrawlProjectListAsync để không lặp lại ~80 dòng script đăng nhập. Nếu lỗi
    /// ở bước nào, tự dọn (Dispose) host trước khi trả về lỗi — bên gọi chỉ cần kiểm tra
    /// err != null.</summary>
    private async Task<(Form? host, WebView2? web, string? error)> OpenLoggedInFsgAsync()
    {
        var (user, pass) = ReadSharedFsgLogin();
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            return (null, null,
                "Chưa có tài khoản FSG đã lưu. Mở \"FSG Yêu cầu\" (FsgRequirementCrawlerForm), đăng nhập 1 lần với tick \"Nhớ thông tin\" trước đã.");
        }

        var host = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Width = 1,
            Height = 1,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Opacity = 0,
        };
        var web = new WebView2 { Dock = DockStyle.Fill };
        host.Controls.Add(web);
        // Trước đây chỉ ép tạo handle (_ = host.Handle) mà không bao giờ Show() — vài máy có
        // thể không init/tải trang ổn định khi control chưa từng "hiện" 1 lần. Giờ gọi Show()
        // thật nhưng Opacity=0 + 1x1px + toạ độ ngoài màn hình nên vẫn vô hình với Bee.
        host.Show();
        // Ép cửa sổ (dù vô hình) thành cửa sổ "active" ở mức OS — cửa sổ không bao giờ được
        // activate có thể khiến Chromium coi trang là nền/không có focus (document.hasFocus()
        // = false), làm các thao tác cần trạng thái "đã chọn/đã focus" trong trang (như chọn 1
        // dòng lưới trước khi bấm "Sửa") không có tác dụng dù JS gọi đúng hàm. Vì cửa sổ nằm
        // ngoài màn hình (-32000,-32000) + Opacity=0 nên việc activate không hiện gì với Bee.
        host.Activate();
        web.Focus();

        try
        {
            // UserDataFolder RIÊNG — tách cookie/session khỏi WebView2 mặc định mà "FSG Yêu
            // cầu"/"FSG dự án" đang dùng, để không bị FSG tự chuyển trang (vì tưởng đã đăng
            // nhập từ cookie cũ) hay đụng độ phiên làm việc với cửa sổ Bee đang mở song song.
            Directory.CreateDirectory(LookupWebViewProfileFolder);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: LookupWebViewProfileFolder);
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.Settings.IsScriptEnabled = true;

            web.CoreWebView2.Navigate(LoginUrl);
            await WaitForPageLoadAsync(web);
            await Task.Delay(600);

            var triggerUnitScript = $@"
            (function() {{
                var txtUser = document.getElementById('LoginExtender_txtUserName');
                if (!txtUser) return 'NO_USER';
                txtUser.focus();
                txtUser.value = '{JsEscape(user)}';
                txtUser.dispatchEvent(new Event('input', {{ bubbles: true }}));
                txtUser.dispatchEvent(new Event('change', {{ bubbles: true }}));

                if (typeof txtUser.onkeypress === 'function') {{
                    txtUser.onkeypress({{ keyCode: 13, which: 13, charCode: 13, preventDefault: function() {{}}, stopPropagation: function() {{}} }});
                }}

                var ext = window.$find ? window.$find('LoginExtender') : null;
                if (ext) {{
                    try {{ ext.executeCommand({{ commandName: 'Change', commandArgument: '1' }}); }} catch(e) {{}}
                }}
                return 'TRIGGERED';
            }})();";

            var triggerResult = await web.ExecuteScriptAsync(triggerUnitScript);
            if (triggerResult.Trim('"') == "NO_USER")
            {
                // Với profile riêng, trường hợp này giờ hiếm khi xảy ra vì "đã có cookie đăng
                // nhập sẵn" — nhưng nếu URL hiện tại KHÔNG còn là Login.aspx (đã tự chuyển
                // trang vì lý do khác), coi như phiên đã sẵn sàng và dùng luôn thay vì báo lỗi.
                var currentUrl = web.CoreWebView2?.Source ?? "";
                if (!currentUrl.Contains("Login.aspx", StringComparison.OrdinalIgnoreCase))
                {
                    return (host, web, null);
                }
                host.Dispose();
                return (null, null, $"Không tìm thấy khung đăng nhập chuẩn FastBusiness trên trang Login FSG (URL hiện tại: {currentUrl}).");
            }

            bool unitLoaded = false;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(400);
                var checkResult = await web.ExecuteScriptAsync(@"
                (function() {
                    var cbo = document.getElementById('LoginExtender_cboUnit');
                    if (!cbo || !cbo.options) return 0;
                    var count = 0;
                    for (var j = 0; j < cbo.options.length; j++) {
                        if (cbo.options[j].text.trim().length > 0 || cbo.options[j].value.trim().length > 0) count++;
                    }
                    return count;
                })();");
                if (int.TryParse(checkResult, out int validOptions) && validOptions > 0) { unitLoaded = true; break; }
            }
            if (!unitLoaded)
            {
                host.Dispose();
                return (null, null, "Không tự nạp được Đơn vị khi đăng nhập FSG.");
            }

            var submitLoginScript = $@"
            (function() {{
                var cbo = document.getElementById('LoginExtender_cboUnit');
                if (cbo && cbo.selectedIndex <= 0 && cbo.options.length > 0) {{
                    for (var j = 0; j < cbo.options.length; j++) {{
                        if (cbo.options[j].text.trim().length > 0) {{ cbo.selectedIndex = j; break; }}
                    }}
                    cbo.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }}

                var txtPass = document.getElementById('LoginExtender_txtPassword');
                if (txtPass) {{
                    txtPass.focus();
                    txtPass.value = '{JsEscape(pass)}';
                    txtPass.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    txtPass.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }}

                var btnOk = document.getElementById('LoginExtender_Ok');
                if (btnOk) btnOk.click();
            }})();";

            await web.ExecuteScriptAsync(submitLoginScript);
            await Task.Delay(1000);

            for (int i = 0; i < 6; i++)
            {
                var confirmResult = await web.ExecuteScriptAsync(AlreadyLoggedInConfirmScript);
                if (confirmResult.Trim('"') == "CONFIRMED") { await Task.Delay(800); break; }
                await Task.Delay(300);
            }

            return (host, web, null);
        }
        catch (Exception ex)
        {
            host.Dispose();
            return (null, null, "Lỗi đăng nhập FSG: " + ex.Message);
        }
    }

    private static string JsEscape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    private static Task WaitForPageLoadAsync(WebView2 web)
    {
        var tcs = new TaskCompletionSource<bool>();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            web.NavigationCompleted -= Handler;
            tcs.TrySetResult(e.IsSuccess);
        }
        web.NavigationCompleted += Handler;
        return tcs.Task;
    }
}