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
/// Giao diện là trang WebView2 (Web/Shell/copysourcestandard.html): cây file có ô tick bên trái, bảng đổi tên bên phải.
/// Trang chỉ gửi số thứ tự của file + tên mới — đường dẫn nguồn lấy từ danh sách C# (<see cref="_files"/>).
/// </summary>
public class CopySourceStandardForm : WebDialogForm
{
    private readonly FileLookupService _fileLookupService;
    private readonly string _sourceRoot;
    private readonly WCommandItem _item;
    private readonly List<string> _files = new();

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
                Js($"copyStd.init({J(new { tree, files = names })})");
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

        var copied = 0;
        var errors = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            try { File.Copy(_files[rows[i].Id], destinations[i], overwrite: true); copied++; }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(_files[rows[i].Id])}: {ex.Message}"); }
        }

        // New files now exist in the source tree — drop File Lookup's cached file list so
        // they show up on its next build instead of after the cache expires.
        if (copied > 0) _fileLookupService.InvalidateCache();

        if (errors.Count > 0)
            MessageBox.Show(this, $"Đã clone {copied}/{rows.Count} file.\nLỗi:\n" + string.Join("\n", errors), "Bcode — Copy source standard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            MessageBox.Show(this, $"Đã clone {copied} file thành công.", "Bcode — Copy source standard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return Task.CompletedTask;
    }
}
