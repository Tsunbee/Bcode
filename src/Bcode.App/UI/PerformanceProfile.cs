using Bcode.App.Models;

namespace Bcode.App.UI;

/// <summary>
/// Chế độ hiệu năng (Thấp / Trung bình / Cao / Tự động) → các thông số cụ thể Bcode dùng để bớt RAM/CPU trên máy yếu.
/// Chỉ tính toán, không tự áp: WebViewEnvironment đọc <see cref="RendererProcessLimit"/> lúc tạo môi trường WebView2,
/// MainForm đọc <see cref="PrewarmSqlTab"/> khi dựng tab SQL dự phòng, RawSqlControl đọc <see cref="MergeSqlResultFrames"/> khi tạo tab,
/// MainForm.Hibernate đọc <see cref="HibernateSqlTabMinutes"/> (0 = tắt) mỗi phút. Tuỳ chỉnh riêng trong AppSettings (khác null) thắng chế độ.
/// Số đo 2026-10-10 (máy dev, chưa mở tab): mỗi WebView2 ~20MB 1 tiến trình; giới hạn 2 tiến trình 500→361MB; tab SQL dựng sẵn ~125MB;
/// gộp 4 trang khung kết quả SQL vào 1 WebView2 (iframe) ~230→182MB.
/// </summary>
public sealed record PerformanceProfile(string Mode, string EffectiveMode, int RendererProcessLimit, bool PrewarmSqlTab, bool MergeSqlResultFrames, int HibernateSqlTabMinutes, bool MergeSqlBarEditor = false)
{
    public const string Auto = "auto", Low = "low", Medium = "medium", High = "high";

    /// <summary>Số luồng CPU và RAM (GB) của máy — dùng cho chế độ Tự động và hiện trong Settings.</summary>
    public static int CpuThreads => Environment.ProcessorCount;
    public static double RamGb { get; } = ReadRamGb();

    /// <summary>Giá trị giới hạn tiến trình đã áp cho phiên đang chạy (null = chưa tạo môi trường) — so với cài đặt để báo "cần mở lại".</summary>
    public static int? AppliedRendererLimit { get; internal set; }

    public static PerformanceProfile Resolve(AppSettings s)
    {
        var mode = s.PerformanceMode is Low or Medium or High ? s.PerformanceMode : Auto;
        var effective = mode == Auto ? DetectMode() : mode;
        var (limit, prewarm, merge, hibernate, mergeEd) = effective switch
        {
            Low => (2, false, true, 15, true),
            Medium => (4, true, true, 30, false),
            _ => (0, true, true, 30, false),
        };
        if (s.RendererProcessLimit is { } l) limit = Math.Clamp(l, 0, 64);
        if (s.PrewarmSqlTab is { } p) prewarm = p;
        if (s.MergeSqlResultFrames is { } m) merge = m;
        if (s.HibernateSqlTabMinutes is { } h) hibernate = Math.Clamp(h, 0, 24 * 60);
        if (s.MergeSqlBarEditor is { } me) mergeEd = me;
        return new PerformanceProfile(mode, effective, limit, prewarm, merge, hibernate, mergeEd);
    }

    /// <summary>Tự động: ≤4 luồng hoặc ≤4GB = Thấp (cùng ngưỡng "Chế độ nhẹ" của BcodeViewer); ≤8 luồng hoặc ≤8GB = Trung bình; còn lại = Cao.</summary>
    public static string DetectMode()
    {
        var ram = RamGb > 0 ? RamGb : double.MaxValue; // không đọc được RAM thì chỉ xét CPU
        if (CpuThreads <= 4 || ram <= 4.5) return Low;
        if (CpuThreads <= 8 || ram <= 8.5) return Medium;
        return High;
    }

    /// <summary>Tham số dòng lệnh thêm cho tiến trình trình duyệt của WebView2 (rỗng = như mặc định).</summary>
    public string BrowserArguments => RendererProcessLimit > 0 ? $"--renderer-process-limit={RendererProcessLimit}" : "";

    private static double ReadRamGb()
    {
        try
        {
            var st = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref st) ? Math.Round(st.TotalPhys / 1073741824.0, 1) : 0;
        }
        catch { return 0; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
