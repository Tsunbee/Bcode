using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Đổi tên file trên cây File Lookup: F2 (phím "fileLookup.rename", đổi được ở Template → Phím tắt) hoặc chuột phải → Rename... = sửa tên ngay trên
/// node như Explorer (chỉ bôi đen phần tên, chừa đuôi), Enter đổi tên thật trên đĩa, Esc huỷ. Cây là TreeView WinForms ở cả 2 chế độ Web/App nên chỉ
/// cài một chỗ này. Không gõ đuôi = giữ đuôi cũ; đổi đuôi thì hỏi; cùng thư mục có file cùng tên khác đuôi (ARTran.xml ↔ ARTran.f) thì hỏi đổi luôn
/// (mặc định Không). Đổi xong: <see cref="FileLookupService.NotifyFileRenamed"/> sửa chỉ mục file + FileParseCache rồi báo mọi tab File Lookup sửa node
/// và khung xem trước (không dựng lại cây); sau đó tra nền chỗ còn nhắc tên cũ (include / controller=...) qua <see cref="SourceIndexService"/> — chỉ báo.
/// </summary>
public partial class FileLookupControl
{
    private TreeNode? _renamingNode;            // node đang mở ô sửa tên — chỉ node này được sửa nhãn (cây vẫn LabelEdit = false ngoài lúc đổi tên)
    private IntPtr _renameEdit;                 // hwnd ô sửa đang mở
    private long _renameStartedAt;
    private bool _renameRetried;
    private CancellationTokenSource? _refCheckCts;
    private readonly SourceIndexService _refIndex = new();

    private const int TVM_GETEDITCONTROL = 0x110F, EM_SETSEL = 0xB1, EM_SETMODIFY = 0xB9, WM_SETTEXT = 0x000C;
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] private static extern IntPtr SendMessageText(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    private void WireRename()
    {
        _tree.BeforeLabelEdit += (_, e) => { if (!ReferenceEquals(e.Node, _renamingNode)) e.CancelEdit = true; };
        _tree.AfterLabelEdit += OnTreeAfterLabelEdit;
        _service.FileRenamed += OnFileRenamed;
        Disposed += (_, _) =>
        {
            _service.FileRenamed -= OnFileRenamed;
            _refCheckCts?.Cancel();
        };
    }

    private void BeginRenameSelected() => BeginRename(_tree.SelectedNode, null);

    /// <param name="text">null = tên hiện tại; khác null = mở lại ô sửa với chữ người dùng vừa gõ (tên trùng / sai ký tự).</param>
    /// <param name="retry">true = lần mở lại sau khi ô sửa bị mất focus ngay lúc vừa mở (xem OnTreeAfterLabelEdit) — không mở lại thêm nữa.</param>
    private void BeginRename(TreeNode? node, string? text, bool retry = false)
    {
        if (IsDisposed || node is not { Tag: FileLookupNode { IsDirectory: false } } || node.TreeView != _tree || node.IsEditing) return;
        _renamingNode = node;
        _renameRetried = retry;
        _renameStartedAt = Environment.TickCount64;
        _tree.LabelEdit = true;
        node.EnsureVisible();
        node.BeginEdit();
        var edit = _renameEdit = SendMessage(_tree.Handle, TVM_GETEDITCONTROL, IntPtr.Zero, IntPtr.Zero);
        if (edit == IntPtr.Zero) return;
        if (text is not null)
        {
            SendMessageText(edit, WM_SETTEXT, IntPtr.Zero, text);
            SendMessage(edit, EM_SETMODIFY, (IntPtr)1, IntPtr.Zero);   // WM_SETTEXT xoá cờ "đã sửa" → Enter ngay sẽ bị TreeView coi như huỷ
        }
        // Như Explorer: chỉ bôi đen phần tên, chừa đuôi (gõ đè là giữ được đuôi cũ).
        var name = text ?? node.Text;
        var baseLen = Path.GetExtension(name).Length > 0 ? name.Length - Path.GetExtension(name).Length : name.Length;
        SendMessage(edit, EM_SETSEL, IntPtr.Zero, (IntPtr)baseLen);
    }

    private void OnTreeAfterLabelEdit(object? sender, NodeLabelEditEventArgs e)
    {
        var node = _renamingNode;
        _renamingNode = null;
        e.CancelEdit = true;   // nhãn chỉ đổi khi đổi tên thật trên đĩa thành công (OnFileRenamed đặt lại)
        var label = e.Label;   // null = Esc hoặc không sửa gì
        // Ô sửa vừa mở đã đóng vì mất focus mà người dùng không bấm chuột / Alt (khung xem trước WebView2 đang nạp file vừa chọn có thể làm
        // luồng UI mất focus — gặp khi bấm F2 ngay sau khi chọn file): mở lại 1 lần. Esc / Enter thì focus vẫn đang ở ô sửa nên không rơi vào đây.
        var stolen = node is not null && !_renameRetried && (label is null || label == node.Text)
                     && Environment.TickCount64 - _renameStartedAt < 1500 && GetFocus() != _renameEdit
                     && MouseButtons == MouseButtons.None && (ModifierKeys & Keys.Alt) == 0;
        // Tắt LabelEdit + đổi tên sau khi TreeView xử lý xong thông báo này (hộp thoại / đổi style ngay trong đó dễ làm rối ô sửa).
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            _tree.LabelEdit = false;
            if (stolen)
            {
                if (ReferenceEquals(_tree.SelectedNode, node)) { _tree.Focus(); BeginRename(node, null, retry: true); }
                return;
            }
            if (label is null || node?.Tag is not FileLookupNode { IsDirectory: false } file) return;
            if (!RenameFileTo(file.FullPath, label)) BeginRename(node, label);
        }));
    }

    private const string RenameTitle = "Bcode — Rename";

    /// <summary>Đổi tên <paramref name="oldPath"/> theo tên người dùng gõ <paramref name="typed"/>.</summary>
    /// <returns>false = tên gõ chưa dùng được (sai ký tự / trùng file có sẵn) → mở lại ô sửa với chữ đã gõ; true = xong / huỷ / lỗi đã báo.</returns>
    private bool RenameFileTo(string oldPath, string typed)
    {
        var dir = Path.GetDirectoryName(oldPath);
        if (string.IsNullOrEmpty(dir)) return true;
        var oldName = Path.GetFileName(oldPath);
        var oldExt = Path.GetExtension(oldName);
        var newName = typed.Trim();
        if (newName.Length == 0 || newName == oldName) return true;

        if (InvalidNameReason(newName) is { } bad)
        {
            MessageBox.Show(this, bad, RenameTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        var newExt = Path.GetExtension(newName);
        if (newExt.Length == 0 && oldExt.Length > 0) { newName += oldExt; newExt = oldExt; }   // không gõ đuôi = giữ đuôi cũ
        else if (!newExt.Equals(oldExt, StringComparison.OrdinalIgnoreCase))
        {
            var keep = newName + oldExt;
            var ask = MessageBox.Show(this,
                $"Đổi đuôi file {(oldExt.Length > 0 ? oldExt : "(không đuôi)")} → {newExt}?\n\nĐổi đuôi có thể làm file không còn được chương trình nạp / mở đúng.\n\n"
                + $"Yes = đổi thành \"{newName}\"\nNo = giữ đuôi cũ: \"{keep}\"\nCancel = thôi không đổi tên",
                RenameTitle, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (ask == DialogResult.Cancel) return true;
            if (ask == DialogResult.No) { newName = keep; newExt = oldExt; }
        }

        var newPath = Path.Combine(dir, newName);
        var caseOnly = newPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase);   // chỉ đổi hoa / thường (ARTran.xml → artran.xml)
        if (caseOnly && newName == oldName) return true;
        if (!caseOnly && (File.Exists(newPath) || Directory.Exists(newPath)))
        {
            MessageBox.Show(this, $"Thư mục này đã có \"{newName}\" — chọn tên khác.\n\n{dir}", RenameTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Cặp cùng tên khác đuôi trong cùng thư mục (ARTran.xml ↔ ARTran.f): hỏi đổi luôn, mặc định Không.
        var moves = new List<(string From, string To)> { (oldPath, newPath) };
        var oldBase = Path.GetFileNameWithoutExtension(oldName);
        var newBase = Path.GetFileNameWithoutExtension(newName);
        if (newBase != oldBase && oldBase.Length > 0)
        {
            List<string> siblings;
            try
            {
                siblings = Directory.GetFiles(dir, oldBase + ".*")
                    .Where(f => !f.Equals(oldPath, StringComparison.OrdinalIgnoreCase)
                                && Path.GetFileNameWithoutExtension(f).Equals(oldBase, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception) { siblings = new(); }   // không liệt kê được thư mục: chỉ đổi file đang chọn
            if (siblings.Count > 0)
            {
                var pairs = siblings.Select(s => (From: s, To: Path.Combine(dir, newBase + Path.GetExtension(s)))).ToList();
                var list = string.Join("\n", pairs.Select(p => $"{Path.GetFileName(p.From)} → {Path.GetFileName(p.To)}"));
                var question = siblings.Count == 1
                    ? $"Đổi tên luôn {Path.GetFileName(siblings[0])}?\n\n{list}"
                    : $"Cùng thư mục còn {siblings.Count} file cùng tên khác đuôi. Đổi tên luôn?\n\n{list}";
                if (MessageBox.Show(this, question, RenameTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    var taken = pairs.Where(p => !p.To.Equals(p.From, StringComparison.OrdinalIgnoreCase) && File.Exists(p.To)).ToList();
                    if (taken.Count > 0)
                    {
                        MessageBox.Show(this, "Không đổi tên được vì thư mục đã có:\n" + string.Join("\n", taken.Select(p => Path.GetFileName(p.To)))
                            + "\n\nChưa đổi file nào — chọn tên khác.", RenameTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                    moves.AddRange(pairs);
                }
            }
        }

        var done = new List<(string From, string To)>();
        foreach (var (from, to) in moves)
        {
            try
            {
                File.Move(from, to);
                done.Add((from, to));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Không đổi tên được {Path.GetFileName(from)}:\n{DescribeRenameError(ex)}"
                    + (done.Count > 0 ? "\n\nĐã đổi: " + string.Join(", ", done.Select(d => $"{Path.GetFileName(d.From)} → {Path.GetFileName(d.To)}")) : ""),
                    RenameTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                break;
            }
        }
        if (done.Count == 0) return true;

        // Sửa chỉ mục file + FileParseCache, rồi mọi tab File Lookup (kể cả tab này) sửa node + khung xem trước — xem OnFileRenamed.
        foreach (var (from, to) in done) _service.NotifyFileRenamed(from, to);
        SetStatus("Đã đổi tên: " + string.Join(", ", done.Select(d => $"{Path.GetFileName(d.From)} → {Path.GetFileName(d.To)}")), false);
        if (!caseOnly) CheckOldNameReferences(oldPath, newPath, done.Select(d => d.To).ToList());
        return true;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Lý do tên file không dùng được trên Windows, null = dùng được.</summary>
    private static string? InvalidNameReason(string name)
    {
        var badChars = name.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0).Distinct().ToList();
        if (badChars.Count > 0)
            return "Tên file không được chứa ký tự: " + string.Join(" ", badChars.Select(c => char.IsControl(c) ? $"(mã {(int)c})" : c.ToString()))
                   + "\n(Không dùng được: \\ / : * ? \" < > |)";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "Tên file không được kết thúc bằng dấu chấm hoặc khoảng trắng.";
        if (ReservedNames.Contains(name.Split('.')[0].Trim())) return $"\"{name.Split('.')[0]}\" là tên dành riêng của Windows, không đặt tên file được.";
        return null;
    }

    private static string DescribeRenameError(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "File không còn ở đó (đã bị xoá / đổi tên ở chỗ khác) — bấm Refresh (F5) để quét lại.",
        UnauthorizedAccessException => "Không có quyền đổi tên (thư mục trên server không cho ghi, hoặc file đang được dùng).\n" + ex.Message,
        IOException when (ex.HResult & 0xFFFF) is 32 or 33 => "File đang được mở / bị khoá bởi chương trình khác (IIS, Visual Studio, Notepad++...). Đóng chương trình đó rồi thử lại.",
        IOException when (ex.HResult & 0xFFFF) is 80 or 183 => "Thư mục đã có file tên này.",
        _ => ex.Message,
    };

    /// <summary>Có tab nào (kể cả tab này) vừa đổi tên file: sửa node mang đường dẫn cũ (tên, đường dẫn, biểu tượng, màu theo đuôi) và nạp lại khung
    /// xem trước nếu đang xem đúng file đó.</summary>
    private void OnFileRenamed(string oldPath, string newPath)
    {
        if (IsDisposed) return;
        var newName = Path.GetFileName(newPath);
        TreeNode? renamed = null;
        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is FileLookupNode { IsDirectory: false } f && f.FullPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    f.Name = newName;
                    f.FullPath = newPath;
                    n.Text = newName;
                    n.ImageKey = n.SelectedImageKey = ImageKeyFor(f);
                    if (!f.HasIssuesInTree) n.ForeColor = ExtensionColor(newName);
                    if (renamed is null || ReferenceEquals(n, _tree.SelectedNode)) renamed = n;
                }
                Walk(n.Nodes);
            }
        }
        Walk(_tree.Nodes);
        if (_previewEditor.CurrentPath is { } current && current.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
            PreviewFile(newPath, renamed);
    }

    // Dòng "tham chiếu" tới file khác: controller="X", <!ENTITY % X SYSTEM "...X.xml">, include, showForm('X'), form=X, tên kèm đuôi file.
    private static readonly Regex ReferenceLine = new(@"controller\s*=|\bSYSTEM\b|<!ENTITY|include|show\$?Form|\bform\s*=|relType|\.(xml|f|ent|dct|txt|js)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Sau khi đổi tên: tra (nền, qua chỉ mục từ của <see cref="SourceIndexService"/> — cùng chỉ mục với "Ai đang dùng" / File Reference) các file
    /// trong App_Data\Controllers còn nhắc tên cũ ở dòng kiểu include / controller=... Chỉ báo, không tự sửa.</summary>
    private async void CheckOldNameReferences(string oldPath, string newPath, List<string> renamedPaths)
    {
        var oldName = Path.GetFileName(oldPath);
        var oldBase = Path.GetFileNameWithoutExtension(oldPath);
        var extOnly = oldBase.Equals(Path.GetFileNameWithoutExtension(newPath), StringComparison.OrdinalIgnoreCase);   // chỉ đổi đuôi: tìm "tên.đuôi cũ"
        if (SourceGrantService.SplitProjectPath(oldPath) is not { } sp) return;
        var appData = Path.Combine(sp.Root, "App_Data");
        // Chỉ mục băm theo từ [\w$#@]+: tên có dấu chấm (Extender.ZVCTran) tra theo từ dài nhất rồi lọc lại cả tên.
        var word = Regex.Matches(oldBase, @"[\w$#@]+").Select(m => m.Value).OrderByDescending(w => w.Length).FirstOrDefault();
        if (word is null || word.Length < 3) return;
        var namePattern = UsageSearchService.NamePattern(oldBase);

        _refCheckCts?.Cancel();
        var cts = _refCheckCts = new CancellationTokenSource();
        var renamedText = _statusLabel.Text;
        void Progress(string msg)
        {
            if (cts.IsCancellationRequested || IsDisposed) return;
            try { BeginInvoke(new Action(() => { if (!cts.IsCancellationRequested) SetStatus($"{renamedText} — đang tìm chỗ còn nhắc \"{oldBase}\": {msg}", false); })); }
            catch (InvalidOperationException) { /* đang đóng */ }
        }

        List<FileReferenceMatch> matches;
        try
        {
            (matches, _, _, _) = await _refIndex.FindLinesAsync(appData, word, false, 2000, Progress, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && !IsDisposed) SetStatus($"{renamedText} — không tìm được chỗ còn nhắc tên cũ: {ex.Message}", false);
            return;
        }
        if (cts.IsCancellationRequested || IsDisposed) return;

        var renamedSet = new HashSet<string>(renamedPaths, StringComparer.OrdinalIgnoreCase);
        var mentions = matches.Where(m => !renamedSet.Contains(m.FilePath) && File.Exists(m.FilePath)
                                          && (extOnly ? m.LineText.Contains(oldName, StringComparison.OrdinalIgnoreCase) : namePattern.IsMatch(m.LineText)))
                              .ToList();
        var refs = mentions.Where(m => ReferenceLine.IsMatch(m.LineText)).ToList();
        var other = mentions.Count - refs.Count;
        var what = extOnly ? oldName : oldBase;
        if (refs.Count == 0)
        {
            SetStatus(renamedText + (other > 0
                ? $" — còn {other} dòng nhắc chữ \"{what}\" ở file khác (không phải include / controller=)"
                : $" — không thấy file nào khác còn nhắc \"{what}\""), false);
            return;
        }

        var controllers = Path.Combine(appData, "Controllers") + Path.DirectorySeparatorChar;
        string Rel(string p) => p.StartsWith(controllers, StringComparison.OrdinalIgnoreCase) ? p[controllers.Length..] : p;
        string Cut(string s) => s.Length <= 140 ? s : s[..140] + "…";
        const int shown = 15;
        var fileCount = refs.Select(m => m.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SetStatus($"{renamedText} — ⚠ tên cũ \"{what}\" còn được nhắc ở {fileCount} file", true);
        MessageBox.Show(this,
            $"Tên cũ \"{what}\" còn được nhắc ở {fileCount} file khác ({refs.Count} dòng) — Bcode KHÔNG tự sửa, sửa tay nếu cần:\n\n"
            + string.Join("\n", refs.Take(shown).Select(m => $"{Rel(m.FilePath)}:{m.LineNumber}   {Cut(m.LineText)}"))
            + (refs.Count > shown ? $"\n… và {refs.Count - shown} dòng nữa" : "")
            + (other > 0 ? $"\n\n(Còn {other} dòng khác chỉ nhắc chữ \"{what}\", không phải include / controller=.)" : ""),
            RenameTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
