using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.ScreenDesigner;

/// <summary>
/// Cầu nối trang ↔ máy. Cùng giao thức với BcodeViewer (Web/hostcall.js): mỗi hàm <c>Begin*(requestId, ...)</c> trả về ngay, việc chạy ở luồng nền,
/// kết quả về trang bằng message <c>hostCallResult</c>. Nhờ vậy dirpreview.js / entity.js của BcodeViewer chạy nguyên bản ở đây.
/// </summary>
public record Screen(string k, string n, string t, string p);

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class DesignerBridge
{
    private readonly Form _form;
    private readonly Action<string> _post;
    private readonly Dictionary<string, string> _opts;

    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BcodeScreenDesigner");
    private static string StatePath => Path.Combine(DataDir, "state.json");

    public DesignerBridge(Form form, Action<string> post, Dictionary<string, string> opts)
    {
        _form = form; _post = post; _opts = opts;
    }

    private void Begin(string id, Func<string> work)
    {
        _ = Task.Run(() =>
        {
            string msg;
            try { msg = JsonSerializer.Serialize(new { type = "hostCallResult", id, ok = true, result = work(), error = (string?)null }); }
            catch (Exception ex) { msg = JsonSerializer.Serialize(new { type = "hostCallResult", id, ok = false, result = (string?)null, error = ex.Message }); }
            try { _post(msg); } catch { /* cửa sổ đang đóng */ }
        });
    }

    /// <summary>Chạy trên luồng giao diện (hộp thoại chọn file / clipboard) rồi trả kết quả cho luồng nền.</summary>
    private T OnUi<T>(Func<T> f) => _form.InvokeRequired ? (T)_form.Invoke(f) : f();

    // ------------------------------------------------------------------------------------------ ngữ cảnh / trạng thái
    private static string? ReadBcodeSettings()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "settings.json");
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }
        catch { return null; }
    }

    /// <summary>Gốc source + project đang chọn (Bcode truyền qua tham số) + danh sách workspace của Bcode (đọc %AppData%\Bcode\settings.json) + trạng thái lần trước.</summary>
    public void BeginGetContext(string requestId) => Begin(requestId, () =>
    {
        var workspaces = new List<object>();
        string viewer = "";
        var raw = ReadBcodeSettings();
        if (raw != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.TryGetProperty("ViewerExePath", out var v) && v.ValueKind == JsonValueKind.String) viewer = v.GetString() ?? "";
                if (root.TryGetProperty("Workspaces", out var ws) && ws.ValueKind == JsonValueKind.Array)
                    foreach (var w in ws.EnumerateArray())
                    {
                        string S(string n) => w.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
                        if (S("SourcePath").Length > 0) workspaces.Add(new { name = S("Name"), id = S("ProjectId"), source = S("SourcePath") });
                    }
            }
            catch { /* settings của Bcode hỏng — bỏ qua */ }
        }
        string state = "{}";
        try { if (File.Exists(StatePath)) state = File.ReadAllText(StatePath); } catch { /* chưa có */ }
        _opts.TryGetValue("source", out var source);
        _opts.TryGetValue("project", out var project);
        _opts.TryGetValue("controller", out var controller);
        _opts.TryGetValue("kind", out var kind);
        return JsonSerializer.Serialize(new { source = source ?? "", project = project ?? "", controller = controller ?? "", kind = kind ?? "", workspaces, viewer, state = JsonDocument.Parse(state).RootElement });
    });

    public void BeginSaveState(string requestId, string json) => Begin(requestId, () =>
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(StatePath, json, new UTF8Encoding(false));
        return "";
    });

    // ------------------------------------------------------------------------------------------ file
    // Bộ nhớ đệm cục bộ: file dưới <source>\App_Data\Controllers đọc lần đầu thì chép về %AppData%\BcodeScreenDesigner\std\<hash> (cùng cây thư mục),
    // các lần sau đọc ở máy — không cần chép trước cả nghìn file. Bật / tắt bằng BeginSetCache.
    private static volatile string _cacheSource = "";
    private static string? CachePathOf(string path)
    {
        var src = _cacheSource; if (src.Length == 0) return null;
        var ctrl = Path.Combine(src, "App_Data", "Controllers").TrimEnd('\\') + "\\";
        if (!path.StartsWith(ctrl, StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(MirrorRoot(src), "App_Data", "Controllers", path.Substring(ctrl.Length));
    }
    public void BeginSetCache(string requestId, string source, string on) => Begin(requestId, () => { _cacheSource = on == "1" ? source : ""; return ""; });

    private static string ReadText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, new UTF8Encoding(false), true);
        return sr.ReadToEnd();
    }
    private static void CopyToCache(string path, string? cp)
    {
        if (cp == null) return;
        try
        {
            var fi = new FileInfo(path);
            Directory.CreateDirectory(Path.GetDirectoryName(cp)!);
            File.Copy(path, cp, true); File.SetLastWriteTimeUtc(cp, fi.LastWriteTimeUtc);
        }
        catch { /* không ghi được cache — lần sau đọc lại nguồn */ }
    }

    // ------------------------------------------------------------------------------------------ dữ liệu mẫu (SampleData/*)
    /// <summary>Các DB FastBusiness trên SQL Server của máy này (nguồn cho "Điền dữ liệu mẫu").</summary>
    public void BeginSampleSources(string requestId) => Begin(requestId, () => JsonSerializer.Serialize(SampleData.SampleDb.ListLocal()));

    /// <summary>Dựng dữ liệu mẫu cho màn hình đang xem — JSON <see cref="SampleData.SampleRequest"/> → <see cref="SampleData.SampleResult"/>.</summary>
    public void BeginSampleData(string requestId, string json) => Begin(requestId, () =>
    {
        var req = JsonSerializer.Deserialize<SampleData.SampleRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        using var db = SampleData.SampleDb.TryOpen(req.Db);
        var res = new SampleData.SampleDataBuilder(req, db).Build();
        if (!string.IsNullOrEmpty(req.Db) && db is null) res.Notes.Insert(0, "Không mở được DB " + req.Db + " — dùng danh mục mẫu + quy tắc.");
        return JsonSerializer.Serialize(res, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    });

    public void BeginReadFile(string requestId, string path) => Begin(requestId, () => ReadCached(path));

    /// <summary>Đọc 1 file: ưu tiên bộ nhớ đệm trên máy, chưa có thì đọc nguồn rồi chép về đệm.</summary>
    private static string ReadCached(string path)
    {
        var cp = CachePathOf(path);
        if (cp != null && File.Exists(cp)) { try { return ReadText(cp); } catch { /* cache hỏng — đọc nguồn */ } }
        var text = ReadText(path);
        CopyToCache(path, cp);
        return text;
    }
    private static string? ReadCachedOrNull(string path) => File.Exists(CachePathOf(path) ?? "") || File.Exists(path) ? ReadCached(path) : null;

    /// <summary>Đọc nhiều include trong 1 lần gọi (entity.js prefetchIncludes) — trước đây Designer thiếu lệnh này nên mỗi file 1 lượt gọi.</summary>
    public void BeginReadFiles(string requestId, string pathsJson) => Begin(requestId, () =>
        JsonSerializer.Serialize(BcodeViewer.App.Host.IncludeTree.ReadMany(JsonSerializer.Deserialize<string[]>(pathsJson) ?? Array.Empty<string>(), ReadCachedOrNull)));

    /// <summary>Đọc cả cây include của màn hình trong 1 lần gọi (entity.js prefetchTree).</summary>
    public void BeginReadIncludeTree(string requestId, string rootPath, string rootText) => Begin(requestId, () =>
        JsonSerializer.Serialize(BcodeViewer.App.Host.IncludeTree.ReadTree(rootPath, rootText, ReadCachedOrNull)));

    public void BeginPathsExist(string requestId, string json) => Begin(requestId, () =>
    {
        var paths = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        return JsonSerializer.Serialize(paths.Select(p => { try { var cp = CachePathOf(p); return (cp != null && File.Exists(cp)) || File.Exists(p); } catch { return false; } }));
    });

    /// <summary>entity.js tìm khai báo entity ở nơi khác trong source — Designer không quét cả project (chậm qua UNC); entity không giải được chỉ bị liệt kê ở chân màn hình.</summary>
    public void BeginSearchWorkspace(string requestId, string root, string pattern, bool a, bool b, bool c, string d, int max) =>
        Begin(requestId, () => "{\"matches\":[]}");

    private static readonly Regex TitleRegex = new(@"<title\b[^>]*\bv=""([^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string Hash(string s)
    {
        var b = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(s.Trim().TrimEnd('\\', '/').ToLowerInvariant()));
        return Convert.ToHexString(b)[..10].ToLowerInvariant();
    }
    private static string IndexPath(string source) => Path.Combine(DataDir, "idx_" + Hash(source) + ".json");

    /// <summary>Quét 1 lần và ghi cache (chậm khi source ở ổ mạng: hàng nghìn file).</summary>
    private static string ScanScreens(string source)
    {
        var ctrl = Path.Combine(source, "App_Data", "Controllers");
        if (!Directory.Exists(ctrl)) throw new DirectoryNotFoundException("Không thấy thư mục App_Data\\Controllers trong: " + source);
        var list = new System.Collections.Concurrent.ConcurrentBag<Screen>();
        foreach (var kind in new[] { "Dir", "Grid", "Filter" })
        {
            var dir = Path.Combine(ctrl, kind);
            if (!Directory.Exists(dir)) continue;
            Parallel.ForEach(Directory.EnumerateFiles(dir, "*.*").Where(f => f.EndsWith(".f", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)), new ParallelOptions { MaxDegreeOfParallelism = 16 }, f =>
            {
                string title = "";
                try
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var buf = new byte[96 * 1024];
                    var n = fs.Read(buf, 0, buf.Length);
                    var head = Encoding.UTF8.GetString(buf, 0, n);
                    var m = TitleRegex.Match(head);
                    if (m.Success) title = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                }
                catch { /* file không đọc được — vẫn liệt kê theo tên */ }
                list.Add(new Screen(kind, Path.GetFileNameWithoutExtension(f), title, f));
            });
        }
        var items = list.OrderBy(x => x.k).ThenBy(x => x.n, StringComparer.OrdinalIgnoreCase).ToList();
        var json = JsonSerializer.Serialize(new { items, stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), cached = false });
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(IndexPath(source), json.Replace("\"cached\":false", "\"cached\":true"), new UTF8Encoding(false)); } catch { /* không ghi được cache — lần sau quét lại */ }
        return json;
    }

    /// <summary>Danh sách màn hình chuẩn {items:[{k,n,t,p}], stamp, cached}. force = "" → có cache thì trả NGAY (không chạm ổ mạng); force = "1" → quét lại và ghi cache.</summary>
    public void BeginListScreens(string requestId, string source, string force) => Begin(requestId, () =>
    {
        if (string.IsNullOrEmpty(force))
        {
            try { var p = IndexPath(source); if (File.Exists(p)) return File.ReadAllText(p); } catch { /* cache hỏng — quét lại */ }
        }
        return ScanScreens(source);
    });

    // ------------------------------------------------------------------------------------------ "Nạp sẵn" source chuẩn về máy
    // Chép Controllers\Dir, Grid, Filter, Include (các file .f/.xml/.ent/.txt... — không chép Report/Dashboard/Main) sang %AppData%\BcodeScreenDesigner\std\<hash>\App_Data\Controllers.
    // Mở màn hình + khai triển entity include đọc từ ổ cục bộ nên nhanh; chép lần sau chỉ lấy file mới / đã đổi.
    private static readonly string[] MirrorDirs = { "Dir", "Grid", "Filter", "Include" };
    private static volatile bool _mirrorRunning;
    private static int _mirrorDone, _mirrorTotal;
    private static string _mirrorError = "";
    private static string MirrorRoot(string source) => Path.Combine(DataDir, "std", Hash(source));

    public void BeginMirrorInfo(string requestId, string source) => Begin(requestId, () =>
    {
        var root = MirrorRoot(source);
        var marker = Path.Combine(root, "mirror.json");
        string stamp = ""; int files = 0;
        try { if (File.Exists(marker)) { using var d = JsonDocument.Parse(File.ReadAllText(marker)); stamp = d.RootElement.GetProperty("stamp").GetString() ?? ""; files = d.RootElement.GetProperty("files").GetInt32(); } } catch { /* chưa có */ }
        return JsonSerializer.Serialize(new { exists = stamp.Length > 0, path = root, stamp, files, running = _mirrorRunning, done = _mirrorDone, total = _mirrorTotal, error = _mirrorError });
    });

    // Mẫu chọn lọc cho "Nạp nhanh": 1 ít danh mục, chứng từ (kèm lưới chi tiết), màn hình lọc báo cáo — đa số màn hình khác cùng cấu trúc.
    private static readonly string[] QuickDir = { "Customer", "Account", "Item", "Currency", "Department", "Bank", "BankAccount", "ItemGroup", "CustomerGroup", "TaxRate", "ARTran", "APTran", "CBTran", "CITran", "CPTran", "GLTran", "IRTran", "IPTran", "SVTran" };
    private static readonly string[] QuickGrid = { "ARTran", "APTran", "CBTran", "CITran", "CPTran", "GLTran", "IRTran", "IPTran", "SVDetail", "SVTran" };
    private static readonly string[] QuickFilter = { "BalanceSheetForm", "ARInvoiceCurrentBalance", "APInvoiceCurrentBalance", "AccountBalanceTransfer", "ARTran", "APTran", "GLTran", "CBTran" };

    /// <summary>mode "full" = chép toàn bộ Dir/Grid/Filter/Include (nhiều nghìn file); còn lại = nạp nhanh mẫu chọn lọc (vài chục file), phần còn lại tự chép khi mở.</summary>
    public void BeginMirrorStart(string requestId, string source, string mode) => Begin(requestId, () =>
    {
        if (_mirrorRunning) return "running";
        var ctrl = Path.Combine(source, "App_Data", "Controllers");
        if (!Directory.Exists(ctrl)) throw new DirectoryNotFoundException("Không thấy thư mục App_Data\\Controllers trong: " + source);
        _mirrorRunning = true; _mirrorDone = 0; _mirrorTotal = 0; _mirrorError = "";
        var root = MirrorRoot(source);
        _ = Task.Run(() =>
        {
            try
            {
                List<string> files;
                if (mode == "full")
                    files = MirrorDirs.Select(d => Path.Combine(ctrl, d)).Where(Directory.Exists)
                        .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)).ToList();
                else
                    files = new[] { ("Dir", QuickDir), ("Grid", QuickGrid), ("Filter", QuickFilter) }
                        .SelectMany(t => t.Item2.SelectMany(n => new[] { ".f", ".xml" }.Select(e => Path.Combine(ctrl, t.Item1, n + e)))).Where(File.Exists).ToList();
                _mirrorTotal = files.Count;
                var target = Path.Combine(root, "App_Data", "Controllers");
                Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 12 }, f =>
                {
                    try
                    {
                        var dest = Path.Combine(target, Path.GetRelativePath(ctrl, f));
                        var fi = new FileInfo(f); var di = new FileInfo(dest);
                        if (!di.Exists || di.Length != fi.Length || di.LastWriteTimeUtc != fi.LastWriteTimeUtc)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            File.Copy(f, dest, true);
                            File.SetLastWriteTimeUtc(dest, fi.LastWriteTimeUtc);
                        }
                    }
                    catch { /* 1 file không chép được (đang bị khoá...) — bỏ qua */ }
                    Interlocked.Increment(ref _mirrorDone);
                });
                // Nạp nhanh: chép luôn CẢ CÂY include của các mẫu (SVTran cần ~584 file ..\Include\*) — trước đây chỉ chép 58 file mẫu nên lần đầu mở
                // mẫu vẫn đọc hàng trăm file qua ổ mạng. Dùng chung IncludeTree với BcodeViewer; file đã có và không đổi thì bỏ qua.
                if (mode != "full")
                {
                    var roots = files.Where(f => f.EndsWith(".f", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.ChangeExtension(f, ".f"))).ToList();
                    var copied = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                    string? CopyInclude(string inc)
                    {
                        if (!File.Exists(inc)) return null;
                        if (inc.StartsWith(ctrl, StringComparison.OrdinalIgnoreCase) && copied.TryAdd(inc, 0))
                        {
                            Interlocked.Increment(ref _mirrorTotal);
                            try
                            {
                                var dest = Path.Combine(target, Path.GetRelativePath(ctrl, inc));
                                var fi = new FileInfo(inc); var di = new FileInfo(dest);
                                if (!di.Exists || di.Length != fi.Length || di.LastWriteTimeUtc != fi.LastWriteTimeUtc)
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                                    File.Copy(inc, dest, true);
                                    File.SetLastWriteTimeUtc(dest, fi.LastWriteTimeUtc);
                                }
                                Interlocked.Increment(ref _mirrorDone);
                                return ReadText(dest);
                            }
                            catch { Interlocked.Increment(ref _mirrorDone); }
                        }
                        // Đã chép ở mẫu trước: đọc bản trên máy, không đọc lại qua ổ mạng cho từng mẫu.
                        var local = inc.StartsWith(ctrl, StringComparison.OrdinalIgnoreCase) ? Path.Combine(target, Path.GetRelativePath(ctrl, inc)) : null;
                        try { if (local != null && File.Exists(local)) return ReadText(local); } catch { /* đọc nguồn */ }
                        return ReadText(inc);
                    }
                    Parallel.ForEach(roots, new ParallelOptions { MaxDegreeOfParallelism = 4 }, f =>
                    {
                        try { BcodeViewer.App.Host.IncludeTree.ReadTree(f, ReadText(f), CopyInclude); }
                        catch { /* 1 mẫu lỗi — các mẫu khác vẫn nạp */ }
                    });
                    files.AddRange(copied.Keys);
                }
                File.WriteAllText(Path.Combine(root, "mirror.json"), JsonSerializer.Serialize(new { stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), files = files.Count, from = source }), new UTF8Encoding(false));
                try { File.Delete(IndexPath(root)); } catch { /* chưa có cache */ }
            }
            catch (Exception ex) { _mirrorError = ex.Message; }
            finally { _mirrorRunning = false; }
        });
        return "started";
    });

    // ------------------------------------------------------------------------------------------ hộp thoại / xuất
    public void BeginPickFolder(string requestId, string initial) => Begin(requestId, () => OnUi(() =>
    {
        using var dlg = new FolderBrowserDialog { Description = "Chọn thư mục source của dự án (thư mục chứa App_Data\\Controllers)", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(initial) ? initial : "" };
        return dlg.ShowDialog(_form) == DialogResult.OK ? dlg.SelectedPath : "";
    }));

    /// <summary>Hộp chọn 1 file (nhập gói thiết kế). Trả đường dẫn, rỗng nếu huỷ.</summary>
    public void BeginPickFile(string requestId, string filter, string initialDir) => Begin(requestId, () => OnUi(() =>
    {
        using var dlg = new OpenFileDialog { Filter = string.IsNullOrWhiteSpace(filter) ? "Tất cả|*.*" : filter, CheckFileExists = true };
        if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir)) dlg.InitialDirectory = initialDir;
        return dlg.ShowDialog(_form) == DialogResult.OK ? dlg.FileName : "";
    }));

    // ------------------------------------------------------------------------------------------ Mẫu của tôi
    private static volatile string _mineDirOverride = "";
    private static string DefaultMineDir => Path.Combine(DataDir, "my");
    private static string MineDir => _mineDirOverride.Length > 0 ? _mineDirOverride : DefaultMineDir;

    /// <summary>Thư mục đang lưu "Mẫu của tôi" (mặc định %AppData%BcodeScreenDesignermy).</summary>
    public void BeginMineInfo(string requestId) => Begin(requestId, () => JsonSerializer.Serialize(new { dir = MineDir, isDefault = _mineDirOverride.Length == 0, defaultDir = DefaultMineDir }));

    /// <summary>Đổi thư mục lưu mẫu (rỗng = về mặc định). Tạo thư mục nếu chưa có.</summary>
    public void BeginSetMineDir(string requestId, string dir) => Begin(requestId, () =>
    {
        dir = (dir ?? "").Trim();
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        _mineDirOverride = dir;
        return MineDir;
    });

    /// <summary>Mở thư mục trong Explorer (tạo nếu chưa có).</summary>
    public void BeginOpenFolder(string requestId, string dir) => Begin(requestId, () =>
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        return "";
    });
    private static string MineSafe(string name)
    {
        var s = string.Concat((name ?? "").Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return s.Length == 0 ? "Mau" : (s.Length > 80 ? s[..80] : s);
    }

    /// <summary>Lưu mẫu vào máy: <c>my\&lt;tên&gt;.f</c> (thiết kế) + <c>my\&lt;tên&gt;.json</c> (dữ liệu test, lưới đã thiết kế). Trùng tên thì ghi đè. Trả tên đã lưu.</summary>
    public void BeginSaveMine(string requestId, string name, string text, string metaJson) => Begin(requestId, () =>
    {
        Directory.CreateDirectory(MineDir);
        var safe = MineSafe(name);
        File.WriteAllText(Path.Combine(MineDir, safe + ".f"), text, new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(MineDir, safe + ".json"), string.IsNullOrWhiteSpace(metaJson) ? "{}" : metaJson, new UTF8Encoding(false));
        return safe;
    });

    public void BeginListMine(string requestId) => Begin(requestId, () =>
    {
        if (!Directory.Exists(MineDir)) return "[]";
        string FromOf(FileInfo f)
        {
            try
            {
                var mp = Path.Combine(MineDir, Path.GetFileNameWithoutExtension(f.Name) + ".json");
                if (!File.Exists(mp)) return "";
                using var doc = JsonDocument.Parse(File.ReadAllText(mp));
                return doc.RootElement.TryGetProperty("from", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() ?? "" : "";
            }
            catch { return ""; }
        }
        var list = new DirectoryInfo(MineDir).GetFiles("*.f").OrderByDescending(f => f.LastWriteTime)
            .Select(f => new { name = Path.GetFileNameWithoutExtension(f.Name), time = f.LastWriteTime.ToString("dd/MM/yyyy HH:mm"), size = f.Length, from = FromOf(f) }).ToList();
        return JsonSerializer.Serialize(list);
    });

    public void BeginReadMine(string requestId, string name) => Begin(requestId, () =>
    {
        var safe = MineSafe(name);
        var f = Path.Combine(MineDir, safe + ".f");
        if (!File.Exists(f)) throw new FileNotFoundException("Không thấy mẫu \"" + name + "\".");
        var metaPath = Path.Combine(MineDir, safe + ".json");
        object meta = new { };
        try { if (File.Exists(metaPath)) meta = JsonDocument.Parse(File.ReadAllText(metaPath)).RootElement.Clone(); } catch { /* meta hỏng — mở thiết kế không kèm dữ liệu */ }
        return JsonSerializer.Serialize(new { text = ReadText(f), meta });
    });

    public void BeginDeleteMine(string requestId, string name) => Begin(requestId, () =>
    {
        var safe = MineSafe(name);
        foreach (var ext in new[] { ".f", ".json" }) { var p = Path.Combine(MineDir, safe + ext); if (File.Exists(p)) File.Delete(p); }
        return "";
    });

    /// <summary>Lưu text (bản thiết kế .f) ra file người dùng chọn. Trả về đường dẫn đã lưu, rỗng nếu huỷ.</summary>
    public void BeginSaveText(string requestId, string defaultName, string filter, string text) => Begin(requestId, () => OnUi(() =>
    {
        using var dlg = new SaveFileDialog { FileName = defaultName, Filter = filter };
        if (dlg.ShowDialog(_form) != DialogResult.OK) return "";
        File.WriteAllText(dlg.FileName, text, new UTF8Encoding(true));
        return dlg.FileName;
    }));

    /// <summary>Nhận ảnh PNG (base64) từ trang: mode "save" = hộp Lưu, "copy" = vào clipboard để dán thẳng vào Word, "both" = cả hai. Trả đường dẫn đã lưu (hoặc "clipboard").</summary>
    public void BeginSavePng(string requestId, string defaultName, string base64, string mode) => Begin(requestId, () => OnUi(() =>
    {
        var bytes = Convert.FromBase64String(base64);
        string result = "";
        if (mode is "copy" or "both")
        {
            using var ms = new MemoryStream(bytes);
            using var bmp = new Bitmap(ms);
            Clipboard.SetImage(new Bitmap(bmp));
            result = "clipboard";
        }
        if (mode is "save" or "both")
        {
            using var dlg = new SaveFileDialog { FileName = defaultName, Filter = "Ảnh PNG (*.png)|*.png" };
            if (dlg.ShowDialog(_form) != DialogResult.OK) return result;
            File.WriteAllBytes(dlg.FileName, bytes);
            result = dlg.FileName;
        }
        return result;
    }));

    /// <summary>Mở file bằng BcodeViewer (đường dẫn lấy từ settings của Bcode) hoặc chọn file trong Explorer.</summary>
    public void BeginOpenFile(string requestId, string path, string viewerExe) => Begin(requestId, () =>
    {
        if (!string.IsNullOrWhiteSpace(viewerExe) && File.Exists(viewerExe))
            Process.Start(new ProcessStartInfo(viewerExe, $"\"{path}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        return "";
    });
}
