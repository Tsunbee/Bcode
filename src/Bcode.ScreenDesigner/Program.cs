namespace Bcode.ScreenDesigner;

internal static class Program
{
    /// <summary>
    /// Tham số (tất cả tuỳ chọn) — Bcode truyền khi mở Designer để "kết nối":
    ///   --source "&lt;thư mục site&gt;"   gốc source (chứa App_Data\Controllers); không có thì lấy lần dùng trước / project đang chọn
    ///   --project "&lt;tên workspace&gt;"  tên project hiện tại (hiện trên tiêu đề)
    ///   --controller &lt;Tên&gt;           mở sẵn controller này (vd SVTran)
    ///   --kind Dir|Grid|Filter         loại của controller trên (mặc định Dir)
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Cần key bản quyền như khi mở từ Bcode (Triển khai → Thiết kế màn hình): chạy thẳng exe mà chưa kích hoạt thì không mở.
        if (!Bcode.App.Services.LicenseService.IsUnlocked)
        {
            MessageBox.Show("Thiết kế màn hình cần key bản quyền.\n\nMở Bcode → Settings → Key bản quyền... để kích hoạt trên máy này, rồi mở lại.",
                "Bcode Screen Designer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].StartsWith("--")) { opts[args[i][2..]] = args[i + 1]; i++; }
        Application.Run(new MainForm(opts));
    }
}
