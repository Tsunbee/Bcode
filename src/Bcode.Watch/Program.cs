namespace Bcode.Watch;

internal static class Program
{
    /// <summary>
    /// Tham số (đều tuỳ chọn — chủ yếu để thử nhiều bản trên 1 máy):
    ///   --name &lt;tên&gt;            tên người xem (mặc định: tên lần trước / tên Windows)
    ///   --host                      vào thẳng chế độ làm chủ phòng (kèm --room &lt;tên phòng&gt;)
    ///   --join &lt;ip[:cổng]&gt;       vào thẳng phòng ở địa chỉ này
    ///   --file &lt;đường dẫn&gt;       (kèm --host) chiếu luôn file video / nhạc trên máy
    ///   --port &lt;n&gt;              cổng TCP điều khiển của phòng (mặc định 47831; phát file dùng 47841)
    ///   --data &lt;thư mục&gt;          nơi lưu watch-lan.json (mặc định %AppData%\Bcode)
    ///   --pos x,y,w,h               vị trí cửa sổ
    ///   --hidden                    chạy ngầm (ngoài màn hình, không taskbar, không giành focus) — thử tự động bằng CDP
    ///   --pass &lt;mật khẩu&gt;       (thử tự động) mật khẩu phòng khi --host / mật khẩu nhập sẵn khi --join — chỉ để thử: lộ trong danh sách tiến trình
    ///   --loopback                 phòng chỉ nghe 127.0.0.1 (thử trên 1 máy, không bật hộp hỏi tường lửa)
    ///   --muted                     tắt tiếng (thử tự động)
    ///   --fakeshare                 nút chia sẻ màn hình phát hình vẽ thử (canvas + tiếng bíp) thay vì chụp màn hình thật (thử WebRTC tự động)
    ///   --fakemic voice|noise|fan|keys|tone  chat voice dùng micro giả (giọng tổng hợp / ồn trắng / quạt / gõ phím / bíp) thay micro thật (thử tự động)
    ///   bcwatch://join/ip:cổng     (không có --) link mời: vào thẳng phòng như --join. Mỗi lần mở thường (không --hidden / --data) đăng ký giao thức này cho tài khoản Windows.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            // bcwatch://join/ip:cổng — Windows gọi khi bấm nút trên trang link mời (Invite.Register)
            if (Invite.IsLink(args[i])) { opts["join"] = Invite.Parse(args[i]); continue; }
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { opts[key] = args[i + 1]; i++; }
            else opts[key] = "1";
        }
        // thử tự động (--hidden / --data) không đụng registry thật
        if (!opts.ContainsKey("hidden") && !opts.ContainsKey("data")) _ = Task.Run(Invite.Register);
        Application.Run(new WatchForm(opts));
    }
}
