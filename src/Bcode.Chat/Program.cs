namespace Bcode.Chat;

internal static class Program
{
    /// <summary>
    /// Tham số (đều tuỳ chọn — chủ yếu để thử nhiều bản trên 1 máy):
    ///   --name &lt;Bhoo ID&gt;      đăng nhập thẳng bằng nick này (bỏ qua màn đăng nhập)
    ///   --data &lt;thư mục&gt;      nơi lưu login.json + users\&lt;id&gt;\ (mặc định %AppData%\Bcode\bhoo)
    ///   --port &lt;n&gt;            cổng TCP nhận tin (mặc định 47861, bận thì +1..+9)
    ///   --disc &lt;n&gt;            cổng UDP tìm bạn (mặc định 47860) — thử 2 bản trên 1 máy: mỗi bản 1 cổng + --peer 127.0.0.1:cổng-của-bản-kia
    ///   --peer &lt;ip[:cổng]&gt;    thêm IP gõ tay (như mục "Thêm IP")
    ///   --loopback             chỉ nghe 127.0.0.1 (thử trên 1 máy, không bật hộp hỏi tường lửa)
    ///   --selftest             tự kiểm phần mạng (2 nút ảo trên 127.0.0.1) rồi thoát; kết quả ghi %TEMP%\Bhoo-selftest.txt
    /// Mỗi tài khoản Windows chỉ chạy 1 bản cho mỗi thư mục dữ liệu: mở lần 2 thì bản đang chạy hiện lên.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { opts[key] = args[i + 1]; i++; }
            else opts[key] = "1";
        }

        if (opts.ContainsKey("selftest"))
        {
            var sw = new StringWriter();
            var code = Net.SelfTest.RunAsync(sw).GetAwaiter().GetResult();
            var path = Path.Combine(Path.GetTempPath(), "Bhoo-selftest.txt");
            try { File.WriteAllText(path, sw.ToString()); } catch { }
            if (!opts.ContainsKey("quiet")) MessageBox.Show(sw.ToString(), "Bhoo — tự kiểm", MessageBoxButtons.OK, code == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return code;
        }

        var key2 = opts.TryGetValue("data", out var d) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(d).ToLowerInvariant())))[..12] : "default";
        using var mutex = new Mutex(true, @"Local\Bhoo-" + key2, out var first);
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Bhoo-show-" + key2);
        if (!first)
        {
            showEvent.Set();   // bản đang chạy hiện danh sách bạn lên
            return 0;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new ChatApp(opts, showEvent));
        GC.KeepAlive(mutex);
        return 0;
    }
}
