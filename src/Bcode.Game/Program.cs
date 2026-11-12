namespace Bcode.Game;

internal static class Program
{
    /// <summary>
    /// Tham số (đều tuỳ chọn — chủ yếu để thử nhiều bản trên 1 máy):
    ///   --name &lt;tên&gt;            tên người chơi (mặc định: tên lần trước / tên Windows)
    ///   --host                      vào thẳng chế độ làm chủ phòng (kèm --room &lt;tên phòng&gt;, --file &lt;xlsx&gt;)
    ///   --join &lt;ip[:cổng]&gt;       vào thẳng phòng ở địa chỉ này
    ///   --port &lt;n&gt;               cổng TCP của phòng (mặc định 47811)
    ///   --min-players &lt;n&gt;        số người tối thiểu để bắt đầu (mặc định 5; nhỏ hơn chỉ để thử)
    ///   --data &lt;thư mục&gt;          nơi lưu game-lan.json (mặc định %AppData%\Bcode)
    ///   --pos x,y,w,h               vị trí cửa sổ (xếp nhiều bản cạnh nhau khi thử)
    ///   --hidden                    chạy ngầm (ngoài màn hình, không taskbar, không giành focus) — thử tự động bằng CDP
    ///   --loopback                 phòng chỉ nghe 127.0.0.1 (thử trên 1 máy, không bật hộp hỏi tường lửa)
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { opts[key] = args[i + 1]; i++; }
            else opts[key] = "1";
        }
        Application.Run(new GameForm(opts));
    }
}
