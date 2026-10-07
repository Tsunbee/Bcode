using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "Library" tool: duyệt / sửa / chèn các snippet SQL/JS/HTML dùng lại. Giao diện là 1 trang WebView2 (Web/Shell/library.html) tự co giãn:
/// danh sách snippet gom theo Category có ô lọc bên trái, ô sửa Name / Category / Nội dung bên phải, thanh dưới cùng hiện ĐƯỜNG DẪN ĐẦY ĐỦ
/// của file snippets.json trên máy này (kèm Copy / Mở thư mục) cùng nút Save và Insert.
///
/// Trang giữ bản sao đang sửa; chỉ khi bấm Save (hoặc Ctrl+S) C# mới thay danh sách của <see cref="SnippetLibraryService"/> rồi ghi xuống file
/// (kèm 1 dòng trong library.log). Insert chèn nội dung snippet đang chọn vào Script Editor (không tự lưu).
/// </summary>
public class LibrarySnippetForm : ThemedForm
{
    private readonly SnippetLibraryService _service;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public string? SelectedContentToInsert { get; private set; }
    private readonly string _currentProject;
    private readonly List<string> _projects;

    public LibrarySnippetForm(SnippetLibraryService service, string currentProject = "", IEnumerable<string>? projects = null)
    {
        _service = service;
        _currentProject = currentProject ?? "";
        _projects = (projects ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Text = "Library (Snippets)";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        Width = 960;
        Height = 660;
        MinimumSize = new Size(480, 420);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        Controls.Add(_web);

        ThemeManager.ThemeChanged += PushTheme;
        FormClosed += (_, _) => ThemeManager.ThemeChanged -= PushTheme;
        Load += async (_, _) => await InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("library.html"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Library", MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    private async void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var action = doc.RootElement.GetProperty("action").GetString();
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;

            switch (action)
            {
                case "ready":
                    PushTheme();
                    var state = new
                    {
                        items = _service.Snippets.Select(s => new { name = s.Name, category = s.Category, content = s.Content, project = s.Project }),
                        path = _service.FilePath,
                        currentProject = _currentProject,
                        projects = _projects,
                    };
                    await Js($"window.init({JsonSerializer.Serialize(state)})");
                    break;
                case "save":
                    await SaveAsync(data);
                    break;
                case "insert":
                    SelectedContentToInsert = Str(data, "content");
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
                case "copyPath":
                    try { Clipboard.SetText(_service.FilePath); } catch { /* clipboard bận */ }
                    await Js("window.saved(true, 'Đã copy đường dẫn.')");
                    break;
                case "openFolder":
                    OpenFolder();
                    break;
                case "close":
                    DialogResult = DialogResult.Cancel;
                    Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            await Js($"window.saved(false, {JsonSerializer.Serialize(ex.Message)})");
        }
    }

    private async Task SaveAsync(JsonElement data)
    {
        var list = new List<Snippet>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                var name = Str(it, "name").Trim();
                var category = Str(it, "category").Trim();
                list.Add(new Snippet
                {
                    Name = name.Length == 0 ? "Snippet" : name,
                    Category = category.Length == 0 ? "General" : category,
                    Content = Str(it, "content"),
                    Project = Str(it, "project").Trim(),
                });
            }
        }

        // Thay danh sách rồi ghi file; lỗi ghi thì trả lại nguyên trạng để người dùng không tưởng đã lưu.
        var backup = _service.Snippets.ToList();
        _service.Snippets.Clear();
        _service.Snippets.AddRange(list);
        try
        {
            _service.Save();
            await Js($"window.saved(true, {JsonSerializer.Serialize($"Đã lưu {list.Count} snippet.")})");
        }
        catch (Exception ex)
        {
            _service.Snippets.Clear();
            _service.Snippets.AddRange(backup);
            await Js($"window.saved(false, {JsonSerializer.Serialize($"Không lưu được vào {_service.FilePath}: {ex.Message}")})");
        }
    }

    private void OpenFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(_service.FilePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(_service.FilePath) ? $"/select,\"{_service.FilePath}\"" : $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { /* không mở được Explorer — bỏ qua */ }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private Task Js(string script) =>
        IsDisposed || _web.CoreWebView2 is null ? Task.CompletedTask : _web.CoreWebView2.ExecuteScriptAsync(script);

    private void PushTheme()
    {
        if (_web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
    }
}
