using System.Data;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

public class FsgRequirementCrawlerForm : Form
{
    // 1. THANH ĐIỀU KHIỂN & LỌC TRÊN ĐỈNH
    private readonly TextBox _txtUser = new() { Width = 95 };
    private readonly TextBox _txtPass = new() { Width = 95, UseSystemPasswordChar = true };
    private readonly TextBox _txtBpLt = new() { Width = 55, Text = "" };
    private readonly TextBox _txtMaDa = new() { Width = 85, Text = "TA_VN" };
    private readonly TextBox _txtMaYc = new() { Width = 70, PlaceholderText = "Mã YC..." };
    private readonly CheckBox _chkAllPages = new() { Text = "Tất cả các trang", AutoSize = true, Checked = true };
    private readonly CheckBox _chkRemember = new() { Text = "Nhớ", AutoSize = true, Checked = true };

    private readonly Button _btnAuto = new() { Text = "▶ Tự động Đăng nhập & Lấy", AutoSize = true, Height = 28 };
    private readonly Button _btnScrapeOnly = new() { Text = "📥 Lấy bảng ngay", AutoSize = true, Height = 28 };

    // 2. THANH LỌC NHANH KẾT QUẢ (TRÊN ĐẦU BẢNG GRID)
    private readonly TextBox _txtQuickFilter = new() { Width = 230, PlaceholderText = "Nhập mã YC hoặc nội dung để lọc..." };
    private readonly Label _lblRecordCount = new() { Text = "0 bản ghi", AutoSize = true, ForeColor = Color.DarkSlateBlue };

    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new("Sẵn sàng");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        ReadOnly = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };


    /// <summary>
    /// Model ánh xạ từng dòng dữ liệu yêu cầu
    /// </summary>
    public class FsgRequirementItem
    {
        public string MaDuAn { get; set; } = "";
        public string PhienBan { get; set; } = "";
        public string BoPhan { get; set; } = "";
        public string MaYeuCau { get; set; } = "";
        public string TenMenu { get; set; } = "";
        public string TrangThai { get; set; } = "";
        public string LapTrinh { get; set; } = "";
        public string NghiepVu { get; set; } = "";
        public string Tester { get; set; } = "";
        public string NgayNhap { get; set; } = "";
        public string NgayDuyet { get; set; } = "";
        public string NgayHoanThanh { get; set; } = "";
        public string FileCount { get; set; } = "0";
        public string NoiDung { get; set; } = "";
        public string SttRec { get; set; } = "";
    }
    // Danh sách gốc lưu toàn bộ yêu cầu tải về từ các trang
    private List<FsgRequirementItem> _allLoadedItems = new();

    private const string LoginUrl = "http://172.168.5.14:81/FSG/Main/Login.aspx";
    private const string TargetUrl = "http://172.168.5.14:81/FSG/Main/nbphyc.aspx?id=05.10.01";
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "fsg_crawler_config.json");

    public FsgRequirementCrawlerForm()
    {
        Text = "FSG - Quản lý & Lọc Yêu cầu FastBusiness";
        Width = 1400;
        Height = 880;
        StartPosition = FormStartPosition.CenterScreen;

        // --- A. THANH CÔNG CỤ ĐỈNH FORM ---
        var topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(6, 6, 6, 4),
            WrapContents = false,
            AutoScroll = true
        };

        void AddLabel(string text) =>
            topBar.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 1, 0) });

        AddLabel("User:");
        topBar.Controls.Add(_txtUser);
        AddLabel("Pass:");
        topBar.Controls.Add(_txtPass);
        AddLabel("Bộ phận:");
        topBar.Controls.Add(_txtBpLt);
        AddLabel("Dự án:");
        topBar.Controls.Add(_txtMaDa);
        AddLabel("Mã YC:");
        topBar.Controls.Add(_txtMaYc);
        topBar.Controls.Add(_chkAllPages);
        topBar.Controls.Add(_chkRemember);
        topBar.Controls.Add(_btnAuto);
        topBar.Controls.Add(_btnScrapeOnly);

        _statusStrip.Items.Add(_statusLabel);

        // --- B. KHUNG CHIA ĐÔI: TRÊN XEM WEB, DƯỚI XEM BẢNG ---
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 370,
            SplitterWidth = 6
        };
        split.Panel1.Controls.Add(_web);

        // Thanh tìm kiếm nhanh ngay trên bảng DataGridView
        var gridToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(6, 5, 6, 2),
            WrapContents = false,
            BackColor = Color.FromArgb(240, 243, 246)
        };
        gridToolbar.Controls.Add(new Label
        {
            Text = "🔍 Tìm nhanh Mã YC / Nội dung:",
            AutoSize = true,
            Margin = new Padding(4, 5, 6, 0),
            Font = new Font(Font, FontStyle.Bold)
        });
        gridToolbar.Controls.Add(_txtQuickFilter);
        _lblRecordCount.Margin = new Padding(12, 5, 4, 0);
        gridToolbar.Controls.Add(_lblRecordCount);

        split.Panel2.Controls.Add(_grid);
        split.Panel2.Controls.Add(gridToolbar);
        gridToolbar.BringToFront();

        Controls.Add(split);
        Controls.Add(topBar);
        Controls.Add(_statusStrip);

        LoadSavedConfig();

        // Sự kiện lọc nhanh khi gõ vào ô tìm kiếm
        _txtQuickFilter.TextChanged += (_, _) => ApplyQuickFilter();

        _btnAuto.Click += async (_, _) => await StartFullAutomationAsync();
        _btnScrapeOnly.Click += async (_, _) => await ScrapeCurrentPageAsync();

        Load += async (_, _) =>
        {
            await _web.EnsureCoreWebView2Async();
            _web.CoreWebView2.Settings.IsScriptEnabled = true;
            _web.CoreWebView2.Settings.IsWebMessageEnabled = true;
            _web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _web.CoreWebView2.Navigate(LoginUrl);
        };
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _btnAuto.Enabled = enabled;
        _btnScrapeOnly.Enabled = enabled;
    }

    private void SetStatus(string text)
    {
        _statusLabel.Text = text;
    }

    // Lọc nhanh trực tiếp trên bộ nhớ mà không cần tải lại trang
    private void ApplyQuickFilter()
    {
        var q = _txtQuickFilter.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            _grid.DataSource = _allLoadedItems.ToList();
            _lblRecordCount.Text = $"Hiển thị: {_allLoadedItems.Count} / {_allLoadedItems.Count} yêu cầu";
        }
        else
        {
            var filtered = _allLoadedItems.Where(x =>
                x.MaYeuCau.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.NoiDung.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.MaDuAn.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.TenMenu.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.LapTrinh.Contains(q, StringComparison.OrdinalIgnoreCase)
            ).ToList();

            _grid.DataSource = filtered;
            _lblRecordCount.Text = $"Tìm thấy: {filtered.Count} / {_allLoadedItems.Count} yêu cầu khớp với '{q}'";
        }
    }

    // =========================================================================
    // LUỒNG 1: TỰ ĐỘNG ĐĂNG NHẬP -> VÀO TRANG -> ĐIỀN LỌC -> TẢI ĐA TRANG
    // =========================================================================
    private async Task StartFullAutomationAsync()
    {
        var user = _txtUser.Text.Trim();
        var pass = _txtPass.Text.Trim();
        var bpLt = _txtBpLt.Text.Trim();
        var maDa = _txtMaDa.Text.Trim();
        var maYc = _txtMaYc.Text.Trim();
        var allPages = _chkAllPages.Checked;

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            MessageBox.Show(this, "Vui lòng nhập User và Password!", "FSG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtUser.Focus();
            return;
        }

        if (_chkRemember.Checked) SaveConfig(user, pass, bpLt, maDa, maYc, allPages);

        SetButtonsEnabled(false);

        try
        {
            SetStatus("Đang mở trang Đăng nhập...");
            _web.CoreWebView2.Navigate(LoginUrl);
            await WaitForPageLoadAsync();
            await Task.Delay(800);

            // BƯỚC 1: ĐIỀN TÊN VÀ KÍCH HOẠT NẠP ĐƠN VỊ
            SetStatus("Đang nhập User và kích hoạt nạp Đơn vị...");
            var triggerUnitScript = $@"
            (function() {{
                var txtUser = document.getElementById('LoginExtender_txtUserName');
                if (!txtUser) return 'NO_USER';
                txtUser.focus();
                txtUser.value = '{user.Replace("'", "\\'")}';
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

            await _web.ExecuteScriptAsync(triggerUnitScript);

            // BƯỚC 2: CHỜ ĐƠN VỊ TẢI XONG
            SetStatus("Đang chờ danh sách Đơn vị từ máy chủ...");
            bool unitLoaded = false;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(400);
                var checkResult = await _web.ExecuteScriptAsync(@"
                (function() {
                    var cbo = document.getElementById('LoginExtender_cboUnit');
                    if (!cbo || !cbo.options) return 0;
                    var count = 0;
                    for (var j = 0; j < cbo.options.length; j++) {
                        if (cbo.options[j].text.trim().length > 0 || cbo.options[j].value.trim().length > 0) count++;
                    }
                    return count;
                })();");

                if (int.TryParse(checkResult, out int validOptions) && validOptions > 0)
                {
                    unitLoaded = true;
                    break;
                }
            }

            if (!unitLoaded)
            {
                SetStatus("Không nạp được Đơn vị tự động. Bạn hãy tự chọn Đơn vị và bấm Đăng nhập tay.");
                MessageBox.Show(this, "Không tự động nạp được Đơn vị!\nBạn có thể tự chọn Đơn vị và đăng nhập trực tiếp trên web, sau đó bấm nút 'Lấy bảng ngay'.", "FSG", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // BƯỚC 3: ĐIỀN PASS VÀ ĐĂNG NHẬP
            SetStatus("Đang điền Mật khẩu và Đăng nhập...");
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
                    txtPass.value = '{pass.Replace("'", "\\'")}';
                    txtPass.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    txtPass.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }}

                var btnOk = document.getElementById('LoginExtender_Ok');
                if (btnOk) btnOk.click();
            }})();";

            await _web.ExecuteScriptAsync(submitLoginScript);
            await Task.Delay(2500);

            // BƯỚC 4: MỞ TRANG YÊU CẦU & KÉO DỮ LIỆU
            SetStatus("Đang mở trang nbphyc.aspx (Cập nhật yêu cầu)...");
            _web.CoreWebView2.Navigate(TargetUrl);
            await WaitForPageLoadAsync();

            await ApplyFilterAndExtractGridAsync(bpLt, maDa, maYc, allPages);
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi: " + ex.Message);
            MessageBox.Show(this, "Có lỗi xảy ra: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    // =========================================================================
    // LUỒNG 2: CHỈ LẤY DỮ LIỆU BẢNG TẠI TRANG HIỆN TẠI (ĐĂNG NHẬP TAY)
    // =========================================================================
    private async Task ScrapeCurrentPageAsync()
    {
        var bpLt = _txtBpLt.Text.Trim();
        var maDa = _txtMaDa.Text.Trim();
        var maYc = _txtMaYc.Text.Trim();
        var allPages = _chkAllPages.Checked;

        SetButtonsEnabled(false);

        try
        {
            var curUrl = _web.CoreWebView2.Source ?? "";
            if (!curUrl.Contains("nbphyc.aspx", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Đang chuyển đến trang cập nhật yêu cầu...");
                _web.CoreWebView2.Navigate(TargetUrl);
                await WaitForPageLoadAsync();
            }

            await ApplyFilterAndExtractGridAsync(bpLt, maDa, maYc, allPages);
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi lấy dữ liệu: " + ex.Message);
            MessageBox.Show(this, "Có lỗi xảy ra: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    // =========================================================================
    // BỘ XỬ LÝ LỌC & CÀO TẤT CẢ CÁC TRANG (PAGINATION RUNNER)
    // =========================================================================
    private async Task ApplyFilterAndExtractGridAsync(string bpLt, string maDa, string maYc, bool allPages)
    {
        // 1. Nếu có Popup "Điều kiện lọc" -> Điền Bộ phận, Dự án, Mã YC rồi bấm 'Nhận'
        SetStatus("Đang kiểm tra Điều kiện lọc...");
        bool filterDialogReady = false;

        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(400);
            var checkDialog = await _web.ExecuteScriptAsync(
                "document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_updateDlgOk') !== null;");

            if (checkDialog == "true")
            {
                filterDialogReady = true;
                break;
            }
        }

        if (filterDialogReady)
        {
            SetStatus("Đang áp dụng điều kiện lọc (Bộ phận, Dự án, Mã YC)...");
            var applyFilterScript = $@"
            (function() {{
                var txtBp = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_form_bp_lt');
                if (txtBp && '{bpLt.Replace("'", "\\'")}') {{
                    txtBp.value = '{bpLt.Replace("'", "\\'")}';
                    txtBp.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }}

                var txtDa = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_form_ma_da');
                if (txtDa && '{maDa.Replace("'", "\\'")}') {{
                    txtDa.value = '{maDa.Replace("'", "\\'")}';
                    txtDa.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }}

                // Điền Mã yêu cầu (fcode1) vào điều kiện lọc máy chủ nếu có
                var txtYc = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_form_fcode1');
                if (txtYc && '{maYc.Replace("'", "\\'")}') {{
                    txtYc.value = '{maYc.Replace("'", "\\'")}';
                    txtYc.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }}

                setTimeout(function() {{
                    var btnOk = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_updateDlgOk');
                    if (btnOk) btnOk.click();
                }}, 250);
            }})();";

            await _web.ExecuteScriptAsync(applyFilterScript);
        }

        // 2. Chờ bảng dữ liệu trang đầu tiên xuất hiện
        SetStatus("Đang chờ bảng dữ liệu xuất hiện...");
        bool gridReady = false;

        for (int i = 0; i < 25; i++)
        {
            await Task.Delay(500);
            var checkGrid = await _web.ExecuteScriptAsync(@"
            (function() {
                var tbl = document.getElementById('ctl00_FastBusiness_MainReport_gridTable');
                return tbl && tbl.querySelectorAll('tr[id*=""gridRow""]').length > 0;
            })();");

            if (checkGrid == "true")
            {
                gridReady = true;
                break;
            }
        }

        if (!gridReady)
        {
            SetStatus("Không tìm thấy dữ liệu yêu cầu trên bảng (hoặc bộ lọc không có bản ghi nào).");
            return;
        }

        // 3. SCRIPT LẶP VÀ CÀO TẤT CẢ CÁC TRANG (PAGINATION)
        SetStatus("Đang đọc dữ liệu từng trang...");
        int maxPagesToFetch = allPages ? 50 : 1; // Giới hạn tối đa 50 trang

        var multiPageScraperScript = $@"
        (async function() {{
            var allRows = [];
            var maxPages = {maxPagesToFetch};
            var pageCount = 0;

            function scrapeCurrentPage() {{
                var table = document.getElementById('ctl00_FastBusiness_MainReport_gridTable');
                if (!table) return [];
                var rows = table.querySelectorAll('tr[id*=""gridRow""]');
                var pageList = [];
                for (var i = 0; i < rows.length; i++) {{
                    var row = rows[i];
                    var rowId = row.id.replace('ctl00_FastBusiness_MainReport_gridRow', '');

                    var getVal = function(colIndex) {{
                        var el = document.getElementById('ctl00_FastBusiness_MainReport_inputCell_' + rowId + '.' + colIndex);
                        return el ? (el.value || '').trim() : '';
                    }};

                    pageList.push({{
                        MaDuAn: getVal(2),
                        PhienBan: getVal(3),
                        BoPhan: getVal(4),
                        TenMenu: getVal(5),
                        SttRec: getVal(6),
                        MaYeuCau: getVal(8),
                        NoiDung: getVal(16),
                        TrangThai: getVal(18),
                        LapTrinh: getVal(19),
                        NghiepVu: getVal(20),
                        Tester: getVal(21),
                        NgayDuyet: getVal(23),
                        NgayHoanThanh: getVal(24),
                        NgayNhap: getVal(32),
                        FileCount: (row.querySelector('.div_file_view_count') ? row.querySelector('.div_file_view_count').innerText.replace(/[() ]/g, '') : '0')
                    }});
                }}
                return pageList;
            }}

            function delay(ms) {{ return new Promise(function(r) {{ setTimeout(r, ms); }}); }}

            function getNextPageLink() {{
                var links = document.querySelectorAll('.GridPager a');
                for (var i = 0; i < links.length; i++) {{
                    if (links[i].innerText.indexOf('Tiếp') >= 0) return links[i];
                }}
                return null;
            }}

            function getCurrentPageNum() {{
                var span = document.querySelector('.GridPager span.Selected');
                return span ? parseInt(span.innerText.trim(), 10) : 1;
            }}

            while (pageCount < maxPages) {{
                pageCount++;
                var curPage = getCurrentPageNum();
                var pageRows = scrapeCurrentPage();
                allRows = allRows.concat(pageRows);

                // Gửi cập nhật tiến độ về C#
                window.chrome.webview.postMessage(JSON.stringify({{
                    action: 'fsg-page-scraped',
                    page: curPage,
                    pageRowCount: pageRows.length,
                    totalSoFar: allRows.length
                }}));

                var nextLink = getNextPageLink();
                if (!nextLink) {{
                    break; // Hết trang
                }}

                // Bấm chuyển sang trang tiếp theo
                nextLink.click();

                // Chờ trang kế tải xong
                var waited = 0;
                var pageChanged = false;
                while (waited < 10000) {{
                    await delay(400);
                    waited += 400;
                    var newPage = getCurrentPageNum();
                    if (newPage !== curPage) {{
                        var tbl = document.getElementById('ctl00_FastBusiness_MainReport_gridTable');
                        if (tbl && tbl.querySelectorAll('tr[id*=""gridRow""]').length > 0) {{
                            pageChanged = true;
                            await delay(300);
                            break;
                        }}
                    }}
                }}

                if (!pageChanged) break; // Quá thời gian chờ tải trang
            }}

            // Gửi toàn bộ dữ liệu về C#
            window.chrome.webview.postMessage(JSON.stringify({{
                action: 'fsg-data-loaded',
                data: allRows
            }}));
        }})();";

        await _web.ExecuteScriptAsync(multiPageScraperScript);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            if (root.TryGetProperty("action", out var act))
            {
                var action = act.GetString();
                if (action == "fsg-page-scraped")
                {
                    var page = root.GetProperty("page").GetInt32();
                    var count = root.GetProperty("pageRowCount").GetInt32();
                    var total = root.GetProperty("totalSoFar").GetInt32();
                    SetStatus($"Đã đọc xong trang {page} (+{count} dòng). Tổng hiện tại: {total} dòng...");
                }
                else if (action == "fsg-data-loaded")
                {
                    var jsonRows = root.GetProperty("data").GetRawText();
                    var dataList = JsonSerializer.Deserialize<List<FsgRequirementItem>>(jsonRows) ?? new();

                    _allLoadedItems = dataList;
                    ApplyQuickFilter();
                    SetStatus($"Hoàn tất! Đã tải về thành công {_allLoadedItems.Count} yêu cầu.");
                    SetButtonsEnabled(true);
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi bóc tách: " + ex.Message);
            SetButtonsEnabled(true);
        }
    }

    private Task WaitForPageLoadAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            _web.NavigationCompleted -= Handler;
            tcs.TrySetResult(e.IsSuccess);
        }
        _web.NavigationCompleted += Handler;
        return tcs.Task;
    }

    private void SaveConfig(string u, string p, string bp, string da, string yc, bool allPages)
    {
        try
        {
            var cfg = new { User = u, Pass = p, BpLt = bp, MaDa = da, MaYc = yc, AllPages = allPages };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg));
        }
        catch { }
    }

    private void LoadSavedConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("User", out var u)) _txtUser.Text = u.GetString();
                if (root.TryGetProperty("Pass", out var p)) _txtPass.Text = p.GetString();
                if (root.TryGetProperty("BpLt", out var bp)) _txtBpLt.Text = bp.GetString();
                if (root.TryGetProperty("MaDa", out var da)) _txtMaDa.Text = da.GetString();
                if (root.TryGetProperty("MaYc", out var yc)) _txtMaYc.Text = yc.GetString();
                if (root.TryGetProperty("AllPages", out var ap)) _chkAllPages.Checked = ap.GetBoolean();
            }
        }
        catch { }
    }
}