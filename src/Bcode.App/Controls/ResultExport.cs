using System.Data;
using System.Diagnostics;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "Save as CSV…" / "Save as Excel (.xlsx)…" của menu chuột phải tab Result trong SQL Query — dùng chung cho chế độ Web
/// (<see cref="MultiResultView"/>, trang resultview.html gửi action "save") và App (<see cref="ResultGridMenu"/> trên lưới của
/// <see cref="NativeResultPane"/>). Hiện SaveFileDialog, ghi file ở luồng nền (<see cref="ResultExportService"/>) — ghi ra file tạm
/// cạnh đích rồi mới đổi tên nên lỗi giữa chừng / file đang mở trong Excel không làm hỏng file cũ — và báo tiến độ / kết quả qua
/// <see cref="Notice"/> (MainForm hiện ở thanh trạng thái, kèm link Mở file / Mở thư mục).
/// </summary>
public static class ResultExport
{
    /// <summary>(cửa sổ chứa lưới, chữ trạng thái, đường dẫn file vừa lưu — null khi đang lưu / lỗi).</summary>
    public static event Action<Form?, string, string?>? Notice;

    private static string? _lastDir;

    /// <param name="host">Control chứa lưới (để tìm cửa sổ cha và tên tab).</param>
    /// <param name="rows">null = mọi dòng của bảng; khác null = chỉ các dòng này (dòng đã chọn), theo đúng thứ tự.</param>
    /// <param name="tableLabel">"Bảng 2" khi lần chạy có nhiều bảng — thêm vào tên file / tên sheet; null khi chỉ 1 bảng.</param>
    /// <param name="quick">Xuất nhanh (phím result.quickExcel, mặc định Ctrl+E): không hỏi nơi lưu — ghi .xlsx vào %TEMP%\Bcode\Export rồi mở luôn bằng Excel.</param>
    public static async Task SaveAsync(Control host, DataTable table, bool xlsx, IReadOnlyList<DataRow>? rows = null, string? tableLabel = null, bool quick = false)
    {
        // Nơi gọi bỏ qua Task (_ = SaveAsync) — lỗi ngoài dự kiến phải hiện ra, đừng để bấm menu mà "không thấy gì".
        try { await SaveCoreAsync(host, table, xlsx, rows, tableLabel, quick); }
        catch (Exception ex)
        {
            Raise(host.FindForm(), "Không lưu được kết quả: " + ex.Message, null);
            MessageBox.Show(host.FindForm(), "Không lưu được kết quả:\n\n" + ex, "Bcode — Save", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static async Task SaveCoreAsync(Control host, DataTable table, bool xlsx, IReadOnlyList<DataRow>? rows, string? tableLabel, bool quick)
    {
        var form = host.FindForm();
        if (quick) xlsx = true;
        var kind = xlsx ? "Excel" : "CSV";
        var ext = xlsx ? ".xlsx" : ".csv";
        string path;
        if (quick) path = QuickPath(DefaultFileName(host, table, tableLabel) + ext);
        else
        {
            using var dlg = new SaveFileDialog
            {
                Title = rows is null ? $"Save as {kind} — {table.Rows.Count:N0} dòng" : $"Save as {kind} — {rows.Count:N0} dòng đã chọn",
                Filter = xlsx ? "Excel Workbook (*.xlsx)|*.xlsx" : "CSV UTF-8 (*.csv)|*.csv",
                FileName = DefaultFileName(host, table, tableLabel) + ext,
                InitialDirectory = _lastDir is not null && Directory.Exists(_lastDir) ? _lastDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                AddExtension = true, DefaultExt = ext, OverwritePrompt = true,
            };
            if (dlg.ShowDialog(form) != DialogResult.OK) return;
            path = dlg.FileName;
            _lastDir = Path.GetDirectoryName(path);
        }

        var count = rows?.Count ?? table.Rows.Count;
        var name = Path.GetFileName(path);
        Raise(form, $"Đang lưu {kind} {name}… 0 / {count:N0} dòng", null);
        var lastTick = 0L;
        var watch = Stopwatch.StartNew();
        var progress = new Progress<int>(n =>
        {
            if (watch.ElapsedMilliseconds - lastTick < 250 || n >= count) return;   // thanh trạng thái tối đa 4 lần/giây
            lastTick = watch.ElapsedMilliseconds;
            Raise(form, $"Đang lưu {kind} {name}… {n:N0} / {count:N0} dòng", null);
        });
        var sheet = tableLabel ?? MeaningfulTableName(table) ?? "Result";
        var temp = Path.Combine(Path.GetDirectoryName(path) ?? "", "~" + Path.GetFileNameWithoutExtension(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            await Task.Run(() =>
            {
                if (xlsx) ResultExportService.WriteXlsx(table, rows, temp, sheet, progress);
                else ResultExportService.WriteCsv(table, rows, temp, progress);
                File.Move(temp, path, overwrite: true);
            });
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* để lại file tạm cũng không sao */ }
            Raise(form, $"Không lưu được {name}: {ex.Message}", null);
            var hint = ex is IOException or UnauthorizedAccessException ? "\n\nFile đang mở trong Excel hoặc thư mục không có quyền ghi? Đóng file / chọn chỗ khác rồi lưu lại." : "";
            MessageBox.Show(form, $"Không ghi được file:\n{path}\n\n{ex.Message}{hint}", "Bcode — Save as " + kind, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var secs = watch.Elapsed.TotalSeconds;
        Raise(form, $"{(quick ? "Xuất nhanh" : "Đã lưu")} {count:N0} dòng × {table.Columns.Count} cột ra {path}" + (secs >= 1 ? $" ({secs:0.#} s)" : ""), path);
        if (quick) OpenFile(path);
    }

    /// <summary>Thư mục của Xuất nhanh: %TEMP%\Bcode\Export (tạo nếu chưa có; file cũ hơn 7 ngày tự dọn, file đang mở trong Excel thì bỏ qua).</summary>
    private static string QuickPath(string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Bcode", "Export");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) try { File.Delete(f); } catch { /* đang mở */ }
        }
        catch { /* không dọn được cũng không sao */ }
        var path = Path.Combine(dir, fileName);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(dir, Path.GetFileNameWithoutExtension(fileName) + $" ({i})" + Path.GetExtension(fileName));
        return path;
    }

    private static void Raise(Form? form, string text, string? path)
    {
        try { Notice?.Invoke(form, text, path); } catch { /* cửa sổ đang đóng */ }
    }

    /// <summary>Tên bảng có nghĩa (không phải "Table"/"Table1" mặc định của DataSet), hoặc null.</summary>
    private static string? MeaningfulTableName(DataTable table)
        => string.IsNullOrWhiteSpace(table.TableName) || table.TableName.StartsWith("Table", StringComparison.OrdinalIgnoreCase) ? null : table.TableName;

    /// <summary>Tên file mặc định: tên tab (+ tên bảng / "Bảng N") + thời gian, vd "SQL Query 2_Bảng 2_20261026_153012".</summary>
    private static string DefaultFileName(Control host, DataTable table, string? tableLabel)
    {
        string? tab = null;
        for (Control? c = host; c is not null; c = c.Parent)
            if (c is TabPage page) { tab = page.Text; break; }
        var parts = new[] { tab?.Replace("*", "").Replace("●", "").Trim(), MeaningfulTableName(table) ?? tableLabel, DateTime.Now.ToString("yyyyMMdd_HHmmss") }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(string.Join("_", parts).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim(' ', '.');
        if (name.Length > 120) name = name[..120];
        return name.Length == 0 ? "Result" : name;
    }

    /// <summary>Link "Mở file" của thanh trạng thái.</summary>
    public static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Không mở được file:\n" + path + "\n\n" + ex.Message, "Bcode"); }
    }

    /// <summary>Link "Mở thư mục": mở Explorer và chọn sẵn file.</summary>
    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* không mở được Explorer — bỏ qua */ }
    }
}
