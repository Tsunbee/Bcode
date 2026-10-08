extern alias rb;
using System.Text.Json;
using Bcode.App.Controls;
using Bcode.App.Models;
using RB = rb::Bcode.ReportBuilder;

namespace Bcode.App.Services;

/// <summary>
/// Cầu nối tới thư viện "Tạo báo cáo" (<c>Libs\Bcode.ReportBuilder.dll</c>). Source của thư viện ở project riêng <c>src/Bcode.ReportBuilder</c>;
/// ở đây chỉ tham chiếu DLL đã build — giống ExcelToFrx.dll / SqlDecryptor.Core.dll. Thư viện tự đứng độc lập, mọi thứ nó cần từ Bcode đi qua <see cref="RB.IReportHost"/>.
/// (extern alias <c>rb</c> để các kiểu dùng chung liên kết từ Bcode.App không bị trùng tên với bản trong DLL.)
/// </summary>
public static class ReportBuilderModule
{
    public static Control CreateControl(DbConnectionService connections, Action<string, bool, string> openSql) =>
        RB.Entry.Create(new Host(connections), openSql);

    private sealed class Host : RB.IReportHost
    {
        private readonly DbConnectionService _c;
        public Host(DbConnectionService c) => _c = c;

        public Microsoft.Data.SqlClient.SqlConnection CreateConnection(bool useSysDatabase) => _c.CreateConnection(useSysDatabase);
        public string WorkspaceName => _c.Current?.Name ?? "";
        public string SourcePath => _c.Current?.SourcePath ?? "";
        public string AppDataDir => BcodePaths.AppData;

        public RB.IWebPage CreatePage(string fileName, string html)
        {
            // trang nhúng trong DLL được ghi ra Web\Shell (cùng thư mục với shell.css) để WebView2 dùng chung nạp; chỉ ghi lại khi nội dung đổi
            var path = Path.Combine(AppContext.BaseDirectory, "Web", "Shell", fileName);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path) || File.ReadAllText(path) != html) File.WriteAllText(path, html, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không ghi được trang giao diện " + path + Environment.NewLine + ex.Message, "Tạo báo cáo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return new Page(new WebBarHost(fileName));
        }

        public async Task<List<RB.MenuRow>> GetMenuAsync()
        {
            // dùng BẢN LƯU menu của Bcode (%AppData%\Bcode\menu-cache, cùng bản lưu của tab WCommand) nên gần như tức thì; chưa có bản lưu thì mới đọc database
            var svc = new WCommandService(_c);
            var flat = svc.LoadCachedFlat();
            if (flat is null || flat.Count == 0)
            {
                flat = new List<WCommandItem>();
                void Walk(IEnumerable<WCommandItem> xs) { foreach (var x in xs) { flat.Add(x); Walk(x.Children); } }
                Walk(await svc.LoadTreeAsync());
            }
            return flat.Select(i => new RB.MenuRow(i.WMenuId ?? "", i.WMenuId0 ?? "", i.Bar ?? "", i.Bar2 ?? "", i.Link ?? "", i.SysId ?? "")).ToList();
        }

        public string MenuScript(string wmenuId, string parentId, string menuId, string barVi, string barEn, string link, string sysId) =>
            WCommandService.GenerateScript(new WCommandItem
            {
                WMenuId = wmenuId, WMenuId0 = parentId, MenuId = menuId, Bar = barVi, Bar2 = barEn, Link = link, SysId = sysId, Status = "1",
            });
    }

    private sealed class Page : RB.IWebPage
    {
        private readonly WebBarHost _h;
        public Page(WebBarHost h) => _h = h;
        public Control View => _h;
        public event Action<JsonElement>? Message { add => _h.Message += value; remove => _h.Message -= value; }
        public event Action? Ready { add => _h.Ready += value; remove => _h.Ready -= value; }
        public void Call(string script) => _h.Call(script);
    }
}
