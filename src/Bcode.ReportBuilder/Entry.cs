
namespace Bcode.ReportBuilder;

/// <summary>Cửa vào của module "Tạo báo cáo" — Bcode gọi <see cref="Create"/> để lấy control cho tab.</summary>
public static class Entry
{
    /// <param name="openSql">(script, dùng Sys Data, tiêu đề tab) — Bcode mở script trong tab SQL.</param>
    /// <param name="transfer">(script, tên procedure, tiêu đề Việt, tiêu đề Anh) — "Chuyển mẫu chạy thử": Bcode đưa procedure sang chế độ "Từ procedure có sẵn".</param>
    public static Control Create(IReportHost host, Action<string, bool, string> openSql, Action<string, string, string, string> transfer)
    {
        ReportBuilderEnv.AppData = host.AppDataDir;
        ReportBuilderEnv.TemplateDir = host.TemplateDir;
        var control = new ReportBuilderControl(host, ReadPageHtml());
        control.OpenSqlRequested += openSql;
        control.TransferRequested += transfer;
        return control;
    }

    /// <summary>Trang giao diện nhúng sẵn trong DLL (reportbuilder.html).</summary>
    private static string ReadPageHtml()
    {
        using var s = typeof(Entry).Assembly.GetManifestResourceStream("reportbuilder.html")
                      ?? throw new InvalidOperationException("Thiếu tài nguyên reportbuilder.html trong Bcode.ReportBuilder.dll");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
