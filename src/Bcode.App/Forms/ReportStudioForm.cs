using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Controls;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Cửa sổ "Tạo báo cáo" (riêng, toàn màn hình — như BcodeViewer — nhưng chạy trong tiến trình Bcode để dùng chung kết nối workspace, theme, tab SQL), 2 chế độ:
/// <list type="bullet">
/// <item><b>① Từ procedure có sẵn</b> (mặc định) — <see cref="QuickReportControl"/>: tham số → Filter, kết quả chạy → Grid, file lấy từ source mẫu CreateReport.</item>
/// <item><b>② Thiết kế từ bảng</b> — thư viện Bcode.ReportBuilder.dll (<see cref="ReportBuilderModule"/>): chọn bảng / trường / cách thể hiện, Bcode sinh procedure zrs_*.</item>
/// </list>
/// Nối 2 chế độ: khi chế độ Thiết kế mở script CREATE PROCEDURE sang tab SQL, thanh trên hiện "Dùng procedure này →" để (sau khi chạy script) chuyển sang
/// chế độ procedure có sẵn và nạp đúng procedure đó; ngược lại ở chế độ procedure có sẵn, "＋ Thiết kế procedure mới…" chuyển sang chế độ Thiết kế.
/// Control của chế độ Thiết kế chỉ tạo khi mở chế độ đó lần đầu.
/// </summary>
public class ReportStudioForm : ThemedForm
{
    public const string ModeDesign = "design", ModeProc = "proc";
    private static readonly Regex CreateProcRx = new(@"\bCREATE\s+(?:OR\s+ALTER\s+)?PROC(?:EDURE)?\s+(?:\[?dbo\]?\.)?\[?([\w$#]+)\]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly WebBarHost _bar = new("reportmodes.html", 44);
    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    private readonly QuickReportControl _quick;
    private readonly DbConnectionService _connections;
    private readonly Action<string, bool, string> _openSql;
    private Control? _designer;
    private string _mode = "";
    private string? _builtProc;

    /// <param name="openSql">(script, dùng Sys Data, tiêu đề tab) — MainForm mở script trong tab SQL.</param>
    public ReportStudioForm(DbConnectionService connections, SqlObjectBrowserService sqlObjects, Action<string, bool, string> openSql, string mode = ModeProc)
    {
        _connections = connections;
        _openSql = openSql;
        Text = "Tạo báo cáo — " + (connections.Current?.Name ?? "");
        if (AppIcons.AppIcon is { } icon) Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(1700, area.Width - 40), Math.Min(980, area.Height - 40));
        MinimumSize = new Size(1000, 640);
        WindowState = FormWindowState.Maximized;

        _quick = new QuickReportControl(sqlObjects, connections) { Visible = false };
        _quick.DesignProcedureRequested += () => SetMode(ModeDesign);
        _body.Controls.Add(_quick);
        Controls.Add(_bar);
        Controls.Add(_body);
        _body.BringToFront();   // Fill dock sau cùng → chiếm phần còn lại dưới thanh chế độ

        _bar.Ready += () => _bar.Call($"bar.init({JsonSerializer.Serialize(new { mode = _mode, proc = _builtProc })})");
        _bar.Message += m =>
        {
            var action = m.TryGetProperty("action", out var a) ? a.GetString() : null;
            var value = m.TryGetProperty("mode", out var v) ? v.GetString() : null;
            BeginInvoke(() =>
            {
                switch (action)
                {
                    case "mode": SetMode(value ?? ModeDesign); break;
                    case "useProc": UseBuiltProcedure(); break;
                    case "dismiss": _builtProc = null; break;
                }
            });
        };
        SetMode(mode);
    }

    /// <summary>Chuyển chế độ (mở lại cửa sổ từ toolbar cũng gọi để đưa về đúng chế độ).</summary>
    public void SetMode(string mode)
    {
        mode = mode == ModeProc ? ModeProc : ModeDesign;
        if (mode == ModeDesign && _designer is null)
        {
            _designer = ReportBuilderModule.CreateControl(_connections, OnDesignerOpenSql, OnDesignerTransfer);
            _designer.Dock = DockStyle.Fill;
            _designer.Visible = false;
            _body.Controls.Add(_designer);
        }
        _mode = mode;
        _quick.Visible = mode == ModeProc;
        if (_designer is not null) _designer.Visible = mode == ModeDesign;
        _bar.Call($"bar.setMode({JsonSerializer.Serialize(mode)})");
    }

    /// <summary>Chế độ Thiết kế mở script sang tab SQL; nếu là script tạo procedure thì nhớ tên để "Dùng procedure này →".</summary>
    private void OnDesignerOpenSql(string script, bool useSys, string title)
    {
        _openSql(script, useSys, title);
        var m = useSys ? null : CreateProcRx.Match(script ?? "");
        if (m is not { Success: true }) return;
        _builtProc = m.Groups[1].Value;
        if (InvokeRequired) BeginInvoke(() => _bar.Call($"bar.onProc({JsonSerializer.Serialize(_builtProc)})"));
        else _bar.Call($"bar.onProc({JsonSerializer.Serialize(_builtProc)})");
    }

    /// <summary>"Chuyển mẫu chạy thử": script đã mở ở tab SQL (OnDesignerOpenSql) — chuyển sang "Từ procedure có sẵn" với đúng procedure đó và tiêu đề đã nhập.</summary>
    private void OnDesignerTransfer(string script, string proc, string titleV, string titleE)
    {
        void Go()
        {
            _builtProc = null;
            _bar.Call("bar.onProc(null)");
            SetMode(ModeProc);
            _quick.LoadProcedure(proc, titleV, titleE);
        }
        if (InvokeRequired) BeginInvoke(Go); else Go();
    }

    private void UseBuiltProcedure()
    {
        if (string.IsNullOrEmpty(_builtProc)) return;
        SetMode(ModeProc);
        _quick.LoadProcedure(_builtProc);
    }
}
