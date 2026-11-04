using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Copy source standard" — WCommand tree's right-click action for cloning a menu item's
/// own source files (resolved the same way Gen Update/File Lookup do, via
/// FileLookupService.BuildTreeForMenuItem) into new files with new names, right next to
/// the originals — e.g. cloning SVTran.xml into SVTranCustom.xml as a starting point for a
/// customized copy. This is the "tick files, clone with a new name" part of FCode's own
/// WCommand > Copy Standard Source; unlike that screen, it doesn't touch any live
/// FastBusiness runtime or menu registration — it only clones files on disk (see
/// WCommandTreeControl's class doc comment for why the rest isn't reproduced here).
/// Bản clone được đổi tên bên trong theo tên file mới (Controller="…" của Main, controller/reportFile/hyperlink trong Grid, Report…)
/// và đổi tiêu đề theo 2 ô Tiêu đề mới V/E — xem <see cref="SourceCopyRenamer"/>. File gốc không bị sửa.
/// Giao diện là trang WebView2 (Web/Shell/copysourcestandard.html): cây file có ô tick bên trái, bảng đổi tên bên phải.
/// Trang chỉ gửi số thứ tự của file + tên mới — đường dẫn nguồn lấy từ danh sách C# (<see cref="_files"/>).
/// </summary>
public class CopySourceStandardForm : WebDialogForm
{
    private readonly FileLookupService _fileLookupService;
    private readonly string _sourceRoot;
    private readonly WCommandItem _item;
    private readonly List<string> _files = new();
    /// <summary>Tiêu đề hiện tại của menu (v/e của Main\*.aspx, không có thì &lt;title&gt; của Grid / Dir) — điền sẵn 2 ô Tiêu đề mới.</summary>
    private SourceCopyRenamer.Title? _oldTitle;

    private SourceCopyRenamer.Title? ReadMenuTitle()
    {
        string? Text(string f)
        {
            try { return SourceCopyRenamer.TryDecode(File.ReadAllBytes(f), out var t, out _, out _) ? t : null; }
            catch (Exception) { return null; }
        }
        bool InFolder(string f, string name) => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), name, StringComparison.OrdinalIgnoreCase);
        foreach (var f in _files.Where(f => InFolder(f, "Main") && Path.GetExtension(f).Equals(".aspx", StringComparison.OrdinalIgnoreCase)))
            if (Text(f) is { } t && SourceCopyRenamer.ReadAspxTitle(t) is { } title) return title;
        foreach (var folder in new[] { "Grid", "Dir" })
            foreach (var f in _files.Where(f => InFolder(f, folder) && Path.GetExtension(f).Equals(".xml", StringComparison.OrdinalIgnoreCase)))
                if (Text(f) is { } t && SourceCopyRenamer.ReadXmlTitle(t) is { } title) return title;
        return null;
    }

    public CopySourceStandardForm(FileLookupService fileLookupService, string sourceRoot, WCommandItem item)
        : base($"Copy source standard — {item.Bar}", "copysourcestandard.html", 1040, 640, 720, 440)
    {
        _fileLookupService = fileLookupService;
        _sourceRoot = sourceRoot;
        _item = item;
    }

    private object ToNode(FileLookupNode node)
    {
        int? id = null;
        if (!node.IsDirectory) { _files.Add(node.FullPath); id = _files.Count - 1; }
        return new { name = node.Name, dir = node.IsDirectory, id, children = node.Children.Select(ToNode).ToList() };
    }

    protected override void OnReady()
    {
        // Dựng cây qua UNC có thể chậm → làm nền, không đứng form.
        _ = Task.Run(() =>
        {
            try
            {
                var root = _fileLookupService.BuildTreeForMenuItem(_sourceRoot, _item.Link, _item.SysId);
                _files.Clear();
                var tree = ToNode(root);
                var names = _files.Select(f => new { dir = Path.GetDirectoryName(f) ?? "", orig = Path.GetFileName(f) }).ToList();
                _oldTitle = ReadMenuTitle();
                Js($"copyStd.init({J(new { tree, files = names, title = new { v = _oldTitle?.V ?? "", e = _oldTitle?.E ?? "" } })})");
            }
            catch (Exception ex) { Js($"copyStd.onStatus({J("Không dựng được cây file: " + ex.Message)}, 'err')"); }
        });
    }

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        if (action != "clone") return Task.CompletedTask;
        var rows = msg.GetProperty("rows").EnumerateArray()
            .Select(r => (Id: r.GetProperty("id").GetInt32(), NewName: (r.GetProperty("newName").GetString() ?? "").Trim())).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Chưa có file nào — tick chọn ở Source File rồi bấm Add.", "Bcode — Copy source standard");
            return Task.CompletedTask;
        }
        if (rows.Any(r => r.NewName.Length == 0 || r.Id < 0 || r.Id >= _files.Count))
        {
            MessageBox.Show(this, "Có dòng chưa nhập New Name.", "Bcode — Copy source standard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Task.CompletedTask;
        }

        var destinations = rows.Select(r => Path.Combine(Path.GetDirectoryName(_files[r.Id]) ?? "", r.NewName)).ToList();
        var existing = destinations.Where(File.Exists).ToList();
        if (existing.Count > 0 &&
            MessageBox.Show(this, $"{existing.Count} file đích đã tồn tại, ghi đè?\n" + string.Join("\n", existing),
                "Bcode — Copy source standard", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return Task.CompletedTask;

        // Đổi tên bên trong bản mới (Controller="…" của Main, tham chiếu trong Grid/Report, tiêu đề) — xem SourceCopyRenamer.
        var conflicts = new List<string>();
        var nameMap = SourceCopyRenamer.BuildNameMap(rows.Select(r => (_files[r.Id], r.NewName)), conflicts);
        SourceCopyRenamer.Title? newTitle = null;
        if (msg.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.Object)
            newTitle = new SourceCopyRenamer.Title(
                t.TryGetProperty("v", out var v) ? (v.GetString() ?? "").Trim() : "",
                t.TryGetProperty("e", out var e) ? (e.GetString() ?? "").Trim() : "");

        var copied = 0;
        var rewritten = 0;
        var errors = new List<string>();
        var notes = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var source = _files[rows[i].Id];
            try
            {
                var result = SourceCopyRenamer.CopyRewriting(source, destinations[i], nameMap, _oldTitle, newTitle);
                copied++;
                if (result == true) rewritten++;
                else if (result is null) notes.Add($"{Path.GetFileName(source)}: không phải UTF-8 — copy nguyên, chưa đổi tên bên trong");
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(source)}: {ex.Message}"); }
        }
        var fCount = rows.Count(r => Path.GetExtension(_files[r.Id]).Equals(".f", StringComparison.OrdinalIgnoreCase));
        if (fCount > 0 && nameMap.Count > 0)
            notes.Add($"{fCount} file .f (đã mã hoá) chỉ copy nguyên — vẫn mang tên cũ bên trong, biên dịch lại từ file .xml mới.");
        if (conflicts.Count > 0) notes.Add("Một tên cũ được đặt nhiều tên mới, dùng tên đầu: " + string.Join("; ", conflicts));

        // New files now exist in the source tree — drop File Lookup's cached file list so
        // they show up on its next build instead of after the cache expires.
        if (copied > 0) _fileLookupService.InvalidateCache();

        var summary = $"Đã clone {copied}/{rows.Count} file, đổi tên/tiêu đề bên trong {rewritten} file."
                      + (notes.Count > 0 ? "\n\nLưu ý:\n" + string.Join("\n", notes) : "");
        if (errors.Count > 0)
            MessageBox.Show(this, summary + "\n\nLỗi:\n" + string.Join("\n", errors), "Bcode — Copy source standard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            MessageBox.Show(this, summary, "Bcode — Copy source standard", MessageBoxButtons.OK, notes.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        return Task.CompletedTask;
    }
}
