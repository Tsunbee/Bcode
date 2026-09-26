using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Bcode.App.Models;
using Bcode.App.UI;

namespace Bcode.App.Forms;

public class ApiDeclarationForm : ThemedForm
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private static string ProfileDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "ApiProfiles");

    public ApiDeclarationForm()
    {
        Text = "Khai báo & Quản lý API";
        Width = 840;
        Height = 650;
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(_web);

        Load += async (_, _) =>
        {
            var shellDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web", "Shell");
            var env = await CoreWebView2Environment.CreateAsync();
            await _web.EnsureCoreWebView2Async(env);

            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.bcode",
                shellDir,
                CoreWebView2HostResourceAccessKind.Allow);

            _web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _web.CoreWebView2.Navigate("https://app.bcode/apideclaration.html");
        };
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var rawJson = e.WebMessageAsJson;
            if (rawJson.StartsWith("\"") && rawJson.EndsWith("\""))
            {
                using var jsonDoc = JsonDocument.Parse(rawJson);
                if (jsonDoc.RootElement.ValueKind == JsonValueKind.String)
                {
                    rawJson = jsonDoc.RootElement.GetString() ?? rawJson;
                }
            }

            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (!root.TryGetProperty("action", out var actionProp)) return;
            var action = actionProp.GetString();

            switch (action)
            {
                case "get-token":
                    await HandleGetTokenAsync(root.GetProperty("data"));
                    break;
                case "test-api":
                    await HandleTestApiAsync(root.GetProperty("data"));
                    break;
                case "browse-file":
                    using (var ofd = new OpenFileDialog { Filter = "Schema (*.json;*.xml;*.docx)|*.json;*.xml;*.docx|All files (*.*)|*.*" })
                    {
                        if (ofd.ShowDialog(this) == DialogResult.OK)
                            await _web.CoreWebView2.ExecuteScriptAsync($"window.setSchemaPath({JsonSerializer.Serialize(ofd.FileName)})");
                    }
                    break;
                case "save-profile":
                    SaveProfile(root.GetProperty("data"));
                    break;
                case "load-profile":
                    LoadProfile();
                    break;
                case "export-postman":
                    ExportPostman(root.GetProperty("data"));
                    break;
                case "close":
                    Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Lỗi: " + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task HandleGetTokenAsync(JsonElement data)
    {
        var tokenUrl = data.TryGetProperty("tokenUrl", out var tu) ? tu.GetString() ?? "" : "";
        var body = data.TryGetProperty("tokenBody", out var tb) ? tb.GetString() ?? "" : "";
        var path = data.TryGetProperty("tokenPath", out var tp) ? tp.GetString() ?? "data.token" : "data.token";

        if (string.IsNullOrWhiteSpace(tokenUrl))
        {
            MessageBox.Show(this, "Vui lòng nhập Link lấy Token.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var res = await client.PostAsync(tokenUrl, content);
            var resStr = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                MessageBox.Show(this, $"Lỗi ({res.StatusCode}):\n{resStr}", "API Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var token = ExtractToken(resStr, path);
            await _web.CoreWebView2.ExecuteScriptAsync($"window.setToken({JsonSerializer.Serialize(token)})");
            MessageBox.Show(this, "Lấy Token thành công!", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Lỗi kết nối: " + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string ExtractToken(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement;
            foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(seg, out var next))
                    current = next;
                else
                {
                    if (doc.RootElement.TryGetProperty("token", out var t1)) return t1.GetString() ?? t1.ToString();
                    if (doc.RootElement.TryGetProperty("access_token", out var t2)) return t2.GetString() ?? t2.ToString();
                    return json;
                }
            }
            return current.GetString() ?? current.ToString();
        }
        catch { return json; }
    }

    private async Task HandleTestApiAsync(JsonElement data)
    {
        var url = data.TryGetProperty("apiUrl", out var au) ? au.GetString() ?? "" : "";
        var token = data.TryGetProperty("token", out var tk) ? tk.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Vui lòng nhập Link API Chính.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            if (!string.IsNullOrWhiteSpace(token))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var res = await client.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"));
            var resStr = await res.Content.ReadAsStringAsync();
            MessageBox.Show(this, $"Status: {(int)res.StatusCode} ({res.StatusCode})\nBody:\n{(resStr.Length > 250 ? resStr[..247] + "..." : resStr)}",
                "Kết quả Test", MessageBoxButtons.OK, res.IsSuccessStatusCode ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Lỗi Test API: " + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveProfile(JsonElement data)
    {
        var proj = data.TryGetProperty("project", out var pj) ? pj.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(proj))
        {
            MessageBox.Show(this, "Vui lòng nhập Mã dự án.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Directory.CreateDirectory(ProfileDir);
        var item = new ApiConfigItem
        {
            ProjectId = proj,
            Department = data.TryGetProperty("dept", out var dp) ? dp.GetString() ?? "" : "",
            SyncType = data.TryGetProperty("syncType", out var st) ? st.GetString() ?? "" : "",
            BaseApiUrl = data.TryGetProperty("apiUrl", out var au) ? au.GetString() ?? "" : "",
            TokenEndpoint = data.TryGetProperty("tokenUrl", out var tu) ? tu.GetString() ?? "" : "",
            TokenRequestBody = data.TryGetProperty("tokenBody", out var tb) ? tb.GetString() ?? "" : "",
            CurrentToken = data.TryGetProperty("token", out var tk) ? tk.GetString() ?? "" : "",
            SchemaFilePath = data.TryGetProperty("schemaPath", out var sp) ? sp.GetString() ?? "" : ""
        };

        File.WriteAllText(Path.Combine(ProfileDir, $"{proj}.json"), JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = true }));
        MessageBox.Show(this, $"Đã lưu cấu hình dự án '{proj}'!", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async void LoadProfile()
    {
        Directory.CreateDirectory(ProfileDir);
        using var ofd = new OpenFileDialog { InitialDirectory = ProfileDir, Filter = "API Profiles (*.json)|*.json" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        var json = File.ReadAllText(ofd.FileName);
        await _web.CoreWebView2.ExecuteScriptAsync($"window.setProfileData({json})");
    }

    private void ExportPostman(JsonElement data)
    {
        var proj = data.TryGetProperty("project", out var pj) ? pj.GetString() ?? "API" : "API";
        using var sfd = new SaveFileDialog
        {
            Filter = "Postman Collection (*.postman_collection.json)|*.postman_collection.json",
            FileName = $"{proj}_Collection.json"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        var collection = new
        {
            info = new { name = proj, schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
            variable = new object[]
            {
                new { key = "base_url", value = data.TryGetProperty("apiUrl", out var au) ? au.GetString() ?? "" : "" },
                new { key = "token", value = data.TryGetProperty("token", out var tk) ? tk.GetString() ?? "" : "" }
            },
            item = new object[]
            {
                new
                {
                    name = "Call API",
                    request = new
                    {
                        method = "POST",
                        header = new[]
                        {
                            new { key = "Content-Type", value = "application/json" },
                            new { key = "Authorization", value = "Bearer {{token}}" }
                        },
                        body = new { mode = "raw", raw = "{}" },
                        url = new { raw = "{{base_url}}", host = new[] { "{{base_url}}" } }
                    }
                }
            }
        };

        File.WriteAllText(sfd.FileName, JsonSerializer.Serialize(collection, new JsonSerializerOptions { WriteIndented = true }));
        MessageBox.Show(this, "Đã xuất file Postman thành công!", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}