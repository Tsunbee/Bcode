using System.Diagnostics;
using System.Text;

namespace Bcode.App.Services;

/// <summary>
/// Tự sinh file Trace Template (.tdf) của SQL Server Profiler — template chỉ có event
/// SQL:BatchStarting (id 13) + filter DatabaseName LIKE %mã% (+ ApplicationName LIKE u_id nếu có).
/// Dùng khi workspace chưa có template: thay vì bấm chuột theo toạ độ vào lưới Events Selection
/// (control tự vẽ, rất dễ lệch), ghi sẵn file template để Profiler nạp như template tự lưu.
///
/// Định dạng .tdf là NHỊ PHÂN (UTF-16LE), suy ra bằng cách đối chiếu 2 file do chính Profiler lưu
/// (kog.tdf, VIETHAI.tdf — trong %AppData%\Microsoft\SQL Profiler\17.0\Templates\...); bộ sinh
/// này tái tạo lại ĐÚNG TỪNG BYTE cả 2 file đó (xem kiểm thử khi viết). Bố cục:
///   0x000  FF FE 90 02 09 00 + "Microsoft SQL Server" (wchar, đệm 0 tới 0x186)
///   0x186  0A 32 40 06 00 00 00 00 (hằng số)
///   0x18E  tên server (wchar, vùng cố định 0x100 byte, đệm 0)
///   0x28E  FA FB ED FB 00 FC FF  — mở đầu danh sách event
///          [len u8][event u16 = 13][cột u16...]    len = 2 + 2*số cột
///          FC FB [len u8][0x001B u16][cột u16...]  cùng tập cột, khác thứ tự
///   FB FB [tổng độ dài các filter u8] rồi từng filter: [cột u16][toán tử u8: 6=LIKE, 7=NOT LIKE]
///          [độ dài u32 (byte, gồm null)][chuỗi wchar + null]
///   (hết file ngay sau null của filter cuối)
/// Các filter CÙNG cột phải đứng liền nhau (cột 10 = ApplicationName trước, cột 35 = DatabaseName sau).
/// </summary>
public static class ProfilerTemplateService
{
    /// <summary>Tập cột mặc định (giống template VIETHAI): ClientProcessID, TextData, LoginName,
    /// DatabaseID, NTUserName, ApplicationName, SPID, StartTime, DatabaseName.</summary>
    public static readonly int[] DefaultEventColumns = { 9, 1, 11, 3, 6, 10, 12, 14, 35 };
    public static readonly int[] DefaultSecondBlockColumns = { 1, 10, 6, 11, 9, 12, 14, 3, 35 };

    private const int EventBatchStarting = 13;
    private const int HeaderLength = 0x28E;
    private const int ServerOffset = 0x18E;
    private const int ServerBufferBytes = 0x100;

    public static byte[] Build(string server, int[] eventColumns, int[] secondBlockColumns,
        string? appLike, string? databaseLike, string profilerExclusionName)
    {
        var header = new byte[HeaderLength];
        new byte[] { 0xFF, 0xFE, 0x90, 0x02, 0x09, 0x00 }.CopyTo(header, 0);
        var provider = Encoding.Unicode.GetBytes("Microsoft SQL Server");
        provider.CopyTo(header, 6);
        new byte[] { 0x0A, 0x32, 0x40, 0x06, 0x00, 0x00, 0x00, 0x00 }.CopyTo(header, 0x186);

        var serverBytes = Encoding.Unicode.GetBytes(server);
        if (serverBytes.Length > ServerBufferBytes - 2)
            throw new ArgumentException("Tên server quá dài cho file template.", nameof(server));
        serverBytes.CopyTo(header, ServerOffset);

        using var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(new byte[] { 0xFA, 0xFB, 0xED, 0xFB, 0x00, 0xFC, 0xFF });
        WriteColumnBlock(ms, EventBatchStarting, eventColumns);
        ms.Write(new byte[] { 0xFC, 0xFB });
        WriteColumnBlock(ms, 0x1B, secondBlockColumns, writeMarker: false);

        // Filter: cột 10 (ApplicationName) gom liền nhau, rồi cột 35 (DatabaseName).
        var filters = new List<byte[]>();
        if (!string.IsNullOrWhiteSpace(appLike)) filters.Add(Filter(10, 6, appLike.Trim()));
        filters.Add(Filter(10, 7, profilerExclusionName));
        if (!string.IsNullOrWhiteSpace(databaseLike)) filters.Add(Filter(35, 6, databaseLike.Trim()));

        var total = filters.Sum(f => f.Length);
        if (total > 255) throw new ArgumentException("Bộ lọc quá dài cho file template.");
        ms.Write(new byte[] { 0xFB, 0xFB, (byte)total });
        foreach (var f in filters) ms.Write(f);
        return ms.ToArray();
    }

    private static void WriteColumnBlock(Stream s, int id, int[] columns, bool writeMarker = true)
    {
        s.WriteByte((byte)(2 + 2 * columns.Length));
        s.Write(BitConverter.GetBytes((ushort)id));
        foreach (var c in columns) s.Write(BitConverter.GetBytes((ushort)c));
    }

    private static byte[] Filter(int column, byte op, string value)
    {
        var text = Encoding.Unicode.GetBytes(value);
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((ushort)column));
        bytes.Add(op);
        bytes.AddRange(BitConverter.GetBytes(text.Length + 2));
        bytes.AddRange(text);
        bytes.Add(0); bytes.Add(0);
        return bytes.ToArray();
    }

    // ------------------------------------------------------------------ đọc lại & cập nhật

    public sealed record TemplateFilter(int Column, byte Op, string Value);
    public sealed record TemplateInfo(string Server, int[] EventColumns, int[] SecondColumns, List<TemplateFilter> Filters)
    {
        /// <summary>Filter ApplicationName LIKE (= ID/u_id), không tính filter loại trừ của Profiler (NOT LIKE).</summary>
        public string? AppLike => Filters.FirstOrDefault(f => f.Column == 10 && f.Op == 6)?.Value;
        public string? DatabaseLike => Filters.FirstOrDefault(f => f.Column == 35 && f.Op == 6)?.Value;
        public string? ProfilerExclusion => Filters.FirstOrDefault(f => f.Column == 10 && f.Op == 7)?.Value;
    }

    /// <summary>Đọc template có đúng bố cục mà <see cref="Build"/> sinh ra (1 event SQL:BatchStarting).
    /// Template khác dạng (nhiều event, định dạng lạ) → null: tuyệt đối không đụng tới.</summary>
    public static TemplateInfo? TryRead(byte[] b)
    {
        try
        {
            var marker = new byte[] { 0xFA, 0xFB, 0xED, 0xFB, 0x00, 0xFC, 0xFF };
            if (b.Length < HeaderLength + marker.Length + 3) return null;
            for (var i = 0; i < marker.Length; i++) if (b[HeaderLength + i] != marker[i]) return null;

            var serverEnd = ServerOffset;
            while (serverEnd + 1 < ServerOffset + ServerBufferBytes && (b[serverEnd] != 0 || b[serverEnd + 1] != 0)) serverEnd += 2;
            var server = Encoding.Unicode.GetString(b, ServerOffset, serverEnd - ServerOffset);

            var p = HeaderLength + marker.Length;
            int[] ReadBlock(int expectedId)
            {
                int len = b[p++];
                if (BitConverter.ToUInt16(b, p) != expectedId) throw new FormatException();
                var cols = new int[(len - 2) / 2];
                for (var i = 0; i < cols.Length; i++) cols[i] = BitConverter.ToUInt16(b, p + 2 + 2 * i);
                p += len;
                return cols;
            }

            var eventCols = ReadBlock(EventBatchStarting);
            if (b[p] != 0xFC || b[p + 1] != 0xFB) return null;
            p += 2;
            var secondCols = ReadBlock(0x1B);
            if (b[p] != 0xFB || b[p + 1] != 0xFB) return null;
            p += 2;
            int total = b[p++];
            var end = p + total;
            if (end != b.Length) return null;

            var filters = new List<TemplateFilter>();
            while (p < end)
            {
                var col = BitConverter.ToUInt16(b, p);
                var op = b[p + 2];
                var len = BitConverter.ToInt32(b, p + 3);
                if (len < 2 || p + 7 + len > end) return null;
                filters.Add(new TemplateFilter(col, op, Encoding.Unicode.GetString(b, p + 7, len - 2)));
                p += 7 + len;
            }
            return new TemplateInfo(server, eventCols, secondCols, filters);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Ghi lại template với filter mới (giữ nguyên server, bộ cột, filter loại trừ Profiler).
    /// Tham số null = giữ filter hiện có. Bản cũ lưu thành "&lt;tên&gt;.tdf.bak" (Profiler chỉ liệt kê *.tdf).</summary>
    public static bool TryUpdateFilters(string path, string? appLike, string? databaseLike)
    {
        try
        {
            var original = File.ReadAllBytes(path);
            var info = TryRead(original);
            if (info == null) return false;
            var rebuilt = Build(info.Server, info.EventColumns, info.SecondColumns,
                appLike ?? info.AppLike, databaseLike ?? info.DatabaseLike,
                info.ProfilerExclusion ?? "SQL Server Profiler - " + Guid.NewGuid().ToString().ToLowerInvariant());
            File.WriteAllBytes(path + ".bak", original);
            File.WriteAllBytes(path, rebuilt);
            return true;
        }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ vị trí & ghi file

    /// <summary>Thư mục template cá nhân của Profiler cho đúng phiên bản SQL Server đích:
    /// %AppData%\Microsoft\SQL Profiler\&lt;phiên bản Profiler&gt;\Templates\Microsoft SQL Server\&lt;1050|120|130...&gt;.
    /// Tên thư mục cuối = ghép số chính + số phụ của ProductVersion (10.50.x → "1050", 12.0.x → "120").</summary>
    public static string? GetUserTemplateFolder(string profilerExePath, string serverProductVersion)
    {
        var parts = serverProductVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;
        var versionFolder = $"{major}{minor}";

        var profilerRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "SQL Profiler");
        string? profilerVersion = null;
        if (Directory.Exists(profilerRoot))
            profilerVersion = Directory.GetDirectories(profilerRoot)
                .Select(Path.GetFileName)
                .Where(n => n != null && Directory.Exists(Path.Combine(profilerRoot, n, "Templates")))
                .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        if (profilerVersion == null)
        {
            try { profilerVersion = FileVersionInfo.GetVersionInfo(profilerExePath).FileMajorPart + ".0"; }
            catch (Exception) { return null; }
        }
        return Path.Combine(profilerRoot, profilerVersion, "Templates", "Microsoft SQL Server", versionFolder);
    }

    /// <summary>Template đã có sẵn (do người dùng tự lưu) thì giữ nguyên; chưa có thì ghi mới.
    /// Trả đường dẫn file và cờ "vừa tạo". null = không xác định được thư mục template.</summary>
    public static (string Path, bool Created)? EnsureTemplate(string profilerExePath, string serverProductVersion,
        string server, string templateName, string? appLike, string? databaseLike)
    {
        if (string.IsNullOrWhiteSpace(templateName) || templateName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;
        var folder = GetUserTemplateFolder(profilerExePath, serverProductVersion);
        if (folder == null) return null;

        var file = System.IO.Path.Combine(folder, templateName.Trim() + ".tdf");
        if (File.Exists(file)) return (file, false);

        // Template dựng sẵn của Profiler (Standard, TSQL, ...) nằm cạnh Profiler.exe — trùng tên thì dùng luôn, không ghi đè.
        var builtInRoot = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(profilerExePath) ?? "", "Templates");
        if (Directory.Exists(builtInRoot) &&
            Directory.EnumerateFiles(builtInRoot, templateName.Trim() + ".tdf", SearchOption.AllDirectories).FirstOrDefault() is { } builtIn)
            return (builtIn, false);

        var exclusion = "SQL Server Profiler - " + Guid.NewGuid().ToString().ToLowerInvariant();
        var bytes = Build(server, DefaultEventColumns, DefaultSecondBlockColumns, appLike, databaseLike, exclusion);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(file, bytes);
        return (file, true);
    }
}
