using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Biên bản xác nhận (Word)": khai báo khách hàng 1 lần → chọn mẫu (cài đặt, báo cáo tài chính, tài liệu khảo sát, nghiệm thu...) → xem trước → xuất Word,
/// 1 hoặc nhiều biên bản cùng lúc. Giao diện là trang WebView2 (Web/Shell/bbxn.html); logic ở <see cref="BbxnService"/>.
/// </summary>
public class BbxnControl : UserControl
{
    private readonly WebBarHost _web = new("bbxn.html") { Dock = DockStyle.Fill };
    private readonly Func<Workspace?> _workspace;
    private static string J(object? o) => JsonSerializer.Serialize(o, BbxnService.Json);

    public BbxnControl(Func<Workspace?> workspace)
    {
        _workspace = workspace;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => HandleSafe(root.GetRawText());
        _web.Ready += SendInit;
    }

    private void SendInit()
    {
        var s = BbxnService.LoadSettings();
        if (string.IsNullOrWhiteSpace(s.MaDa)) s.MaDa = _workspace()?.ProjectId ?? "";
        _web.Call($"bbxn.init({J(new { settings = s, templates = BbxnService.LoadTemplates(), projectId = _workspace()?.ProjectId ?? "" })})");
    }

    private void HandleSafe(string raw)
    {
        try { Handle(raw); }
        catch (Exception ex) { _web.Call($"bbxn.onError({J(ex.Message)})"); }
    }

    private static BbxnState StateOf(JsonElement el) => el.Deserialize<BbxnState>(BbxnService.Json) ?? new BbxnState();

    private void Handle(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        switch (root.GetProperty("action").GetString())
        {
            case "ready": SendInit(); break;

            case "preview":
            {
                var st = StateOf(root.GetProperty("state"));
                var templates = BbxnService.LoadTemplates().ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
                var numbers = st.Docs.Select(d => templates.TryGetValue(d.TemplateId, out var t) ? BbxnService.DocNumber(st, d, t) : "").ToList();
                var index = root.TryGetProperty("index", out var ie) ? ie.GetInt32() : 0;
                var html = "";
                if (index >= 0 && index < st.Docs.Count && templates.TryGetValue(st.Docs[index].TemplateId, out var tpl))
                    html = BbxnService.ToHtml(BbxnService.BuildBlocks(st, st.Docs[index], tpl));
                _web.Call($"bbxn.onPreview({J(new { html, numbers })})");
                break;
            }

            case "saveSettings":
            {
                var s = root.GetProperty("settings").Deserialize<BbxnService.Settings>(BbxnService.Json) ?? new BbxnService.Settings();
                BbxnService.SaveSettings(s);
                break;
            }

            case "export": Export(StateOf(root.GetProperty("state")), root.GetProperty("mode").GetString() == "many"); break;

            case "importTpl":
            {
                using var dlg = new OpenFileDialog { Title = "Import mẫu biên bản", Filter = "Mẫu (*.json;*.docx)|*.json;*.docx|Tất cả|*.*", Multiselect = true };
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) break;
                var n = 0;
                foreach (var f in dlg.FileNames)
                    foreach (var t in BbxnService.ImportFile(f)) { BbxnService.SaveTemplate(t); n++; }
                _web.Call($"bbxn.onTemplates({J(BbxnService.LoadTemplates())}, {J($"Đã import {n} mẫu.")})");
                break;
            }

            case "exportTpl":
            {
                var ids = root.TryGetProperty("ids", out var arr) ? arr.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
                var list = BbxnService.LoadTemplates().Where(t => ids.Count == 0 || ids.Contains(t.Id)).ToList();
                using var dlg = new SaveFileDialog { Title = "Xuất mẫu biên bản", Filter = "Mẫu biên bản (*.json)|*.json", FileName = list.Count == 1 ? $"mau-{list[0].Abbr}.json" : "mau-bien-ban.json" };
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) break;
                BbxnService.ExportTemplates(dlg.FileName, list);
                _web.Call($"bbxn.onToast({J($"Đã xuất {list.Count} mẫu → {dlg.FileName}")})");
                break;
            }

            case "saveTpl":
            {
                var t = root.GetProperty("template").Deserialize<BbxnTemplate>(BbxnService.Json);
                if (t is null) break;
                BbxnService.SaveTemplate(t);
                _web.Call($"bbxn.onTemplates({J(BbxnService.LoadTemplates())}, {J("Đã lưu mẫu " + t.Name)}, {J(t.Id)})");
                break;
            }

            case "deleteTpl":
            {
                var id = root.GetProperty("id").GetString() ?? "";
                BbxnService.DeleteTemplate(id);
                _web.Call($"bbxn.onTemplates({J(BbxnService.LoadTemplates())}, {J("Đã xoá / khôi phục mẫu gốc.")}, {J(id)})");
                break;
            }
        }
    }

    private void Export(BbxnState st, bool many)
    {
        if (st.Docs.Count == 0) { _web.Call($"bbxn.onError({J("Chưa có biên bản nào trong danh sách.")})"); return; }
        var templates = BbxnService.LoadTemplates().ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var docs = new List<(string Number, List<BbxnBlock> Blocks)>();
        foreach (var d in st.Docs)
        {
            if (!templates.TryGetValue(d.TemplateId, out var tpl)) continue;
            docs.Add((BbxnService.DocNumber(st, d, tpl), BbxnService.BuildBlocks(st, d, tpl)));
        }
        if (docs.Count == 0) return;

        string? opened = null;
        if (many)
        {
            using var dlg = new FolderBrowserDialog { Description = "Chọn thư mục lưu các biên bản (mỗi biên bản 1 file Word)", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            foreach (var (number, blocks) in docs)
                BbxnService.WriteDocx(Path.Combine(dlg.SelectedPath, BbxnService.FileNameFor(number) + ".docx"), new[] { blocks });
            opened = dlg.SelectedPath;
        }
        else
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Xuất biên bản ra Word", Filter = "Word (*.docx)|*.docx",
                FileName = docs.Count == 1 ? BbxnService.FileNameFor(docs[0].Number) + ".docx" : $"{(string.IsNullOrWhiteSpace(st.MaDa) ? "BienBan" : st.MaDa)}_BienBan_{DateTime.Now:yyyyMMdd}.docx",
            };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            BbxnService.WriteDocx(dlg.FileName, docs.Select(d => d.Blocks).ToList());
            opened = dlg.FileName;
        }
        _web.Call($"bbxn.onExported({J(new { count = docs.Count, path = opened, many })})");
        try { Process.Start(new ProcessStartInfo("explorer.exe", many ? $"\"{opened}\"" : $"/select,\"{opened}\"") { UseShellExecute = true }); } catch { /* không mở được Explorer — bỏ qua */ }
    }
}
