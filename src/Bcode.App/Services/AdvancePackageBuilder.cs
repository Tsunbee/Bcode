using System.Text.RegularExpressions;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>
/// Phần nghiệp vụ của "Note (New)" (Advance Note) tách khỏi giao diện để hai giao diện (trang WebView2 <c>AdvanceNoteControl</c> và bản WinForms <c>NativeAdvanceNoteControl</c>)
/// dùng chung: gom gói update từ các y/c (<see cref="BuildAsync"/>), xếp thứ tự chạy (<see cref="OrderForRun"/>), đồng bộ y/c từ quản lý yêu cầu (<see cref="SyncAsync"/>).
/// </summary>
public static class AdvancePackageBuilder
{
    /// <summary>File đứng trước, script sau và xếp theo database rồi tên file (00_Table → 01_script_top → 01_view → 02_* → 03_script_bottom) = thứ tự nên chạy.</summary>
    public static List<PackageItem> OrderForRun(IEnumerable<PackageItem> items) =>
        items.Where(i => !i.IsScript).Concat(items.Where(i => i.IsScript)
            .OrderBy(i => i.RelativeDestPath.Replace('/', '\\').Contains("\\sys\\", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(i => Path.GetFileName(i.RelativeDestPath), StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Nội dung note.txt của gói: Note của các y/c đã chọn (bỏ y/c không có note); từ 2 y/c có note trở lên thì mỗi khối có tên y/c đứng đầu.</summary>
    public static string NoteText(IEnumerable<AdvanceRequest> picked)
    {
        var withNote = picked.Where(r => !string.IsNullOrWhiteSpace(r.Note)).ToList();
        return withNote.Count == 1
            ? withNote[0].Note.Trim()
            : string.Join("\r\n\r\n", withNote.Select(r => "[" + r.Name + "]\r\n" + r.Note.Trim()));
    }

    public static string SafeFolder(string? name)
    {
        var s = string.Concat((name ?? "").Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return s.Length == 0 ? "request" : s;
    }

    /// <summary>Gom script/file của các y/c đã chọn thành 1 gói (1 y/c: phẳng; nhiều y/c: mỗi y/c 1 thư mục con) — dùng chung cho Xem trước gói và Generate Update.</summary>
    public static async Task<GenAllResult> BuildAsync(GenAllService genAll, Workspace ws, List<AdvanceRequest> picked, bool split = true)
    {
        var merged = new GenAllResult();
        if (!split && picked.Count > 1)
        {
            // Gộp chung: mọi y/c vào 1 gói phẳng — 1 APP_Script.sql / SYS_Script.sql sắp theo thứ tự table → top → view → stored → bottom, file trùng đường dẫn chỉ lấy 1 lần.
            // Top / Bottom Script của từng y/c khác nhau thì nối hết (không bỏ y/c nào) — cùng nội dung chỉ lấy 1 lần.
            var topBottom = new Dictionary<string, (string Origin, List<string> Parts)>(StringComparer.OrdinalIgnoreCase);
            foreach (var req in picked)
            {
                var one = await genAll.ResolveAsync(ws, req, combine: false);
                foreach (var item in one.Items)
                {
                    if (item.Origin is "SQL Top Script" or "SQL Bottom Script" && item.GeneratedContent is not null)
                    {
                        if (!topBottom.TryGetValue(item.RelativeDestPath, out var tb)) topBottom[item.RelativeDestPath] = tb = (item.Origin, new List<string>());
                        if (!tb.Parts.Contains(item.GeneratedContent)) tb.Parts.Add(item.GeneratedContent);
                        continue;
                    }
                    merged.Add(item);
                }
                merged.Warnings.AddRange(one.Warnings.Select(w => $"[{req.Name}] {w}"));
            }
            foreach (var (path, tb) in topBottom)
                merged.Add(new PackageItem { Origin = tb.Origin, RelativeDestPath = path, GeneratedContent = string.Join("\r\nGO\r\n", tb.Parts) });
            GenAllService.CombineScripts(merged);
            return merged;
        }
        var multi = picked.Count > 1;
        // Nhiều y/c: mỗi y/c 1 thư mục con riêng (<tên y/c>\...). Top/Bottom Script giống hệt nhau giữa các y/c thì dùng chung ở gốc gói.
        var perReq = new List<(string Sub, GenAllResult Res)>();
        foreach (var req in picked)
        {
            var one = await genAll.ResolveAsync(ws, req);
            perReq.Add((SafeFolder(req.Name), one));
            merged.Warnings.AddRange(one.Warnings.Select(w => $"[{req.Name}] {w}"));
        }
        if (!multi) { foreach (var item in perReq[0].Res.Items) merged.Add(item); }
        else
        {
            static bool IsShared(PackageItem i) => i.Origin is "SQL Top Script" or "SQL Bottom Script";
            var sharedOk = perReq.SelectMany(p => p.Res.Items).Where(IsShared).GroupBy(i => i.RelativeDestPath, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(i => i.GeneratedContent).Distinct().Count() == 1)
                .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (sub, res) in perReq)
                foreach (var item in res.Items)
                {
                    if (IsShared(item) && sharedOk.Contains(item.RelativeDestPath)) { merged.Add(item); continue; }
                    merged.Add(new PackageItem
                    {
                        Origin = item.Origin,
                        RelativeDestPath = Path.Combine(sub, item.RelativeDestPath),
                        SourceFilePath = item.SourceFilePath,
                        GeneratedContent = item.GeneratedContent,
                    });
                }
        }
        return merged;
    }

    /// <summary>Nội dung của 1 mục gói để xem nhanh: script đã sinh, hoặc nội dung file nhỏ.</summary>
    public static string ItemText(PackageItem it)
    {
        if (it.GeneratedContent is not null) return it.GeneratedContent;
        try
        {
            var fi = new FileInfo(it.SourceFilePath ?? "");
            return !fi.Exists ? "(file không tồn tại)" : fi.Length > 400_000 ? $"(file {fi.Length / 1024:N0} KB — quá lớn để xem nhanh)" : File.ReadAllText(fi.FullName);
        }
        catch (Exception ex) { return "(không đọc được: " + ex.Message + ")"; }
    }

    /// <summary>
    /// "Sync yêu cầu": kéo các yêu cầu của lập trình viên (ma_lt1) trong project đang vào từ quản lý yêu cầu (bảng nvphyc) và tạo y/c mới
    /// cho mỗi mã yêu cầu (fcode1) chưa có trong danh sách — y/c đã có (trùng tên, không phân biệt hoa/thường) giữ nguyên, không ghi đè.
    /// Nội dung y/c mới được điền sẵn bộ phận / mã nhân viên / người lập trình lấy về. Trả (thành công, thông báo, danh sách có thay đổi cần lưu).
    /// </summary>
    public static async Task<(bool Ok, string Message, bool Changed)> SyncAsync(List<AdvanceRequest> requests, Workspace? ws, string programmer)
    {
        programmer = programmer.Trim();
        var code = ws is null ? "" : (string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.Name : ws.ProjectId).Trim();
        if (code.Length == 0) return (false, "Chưa chọn project.", false);
        if (programmer.Length == 0) return (false, "Nhập tên lập trình rồi bấm Sync.", false);

        var (rows, error) = await new FsgProjectLookupService().FetchRequestsAsync(code, programmer);
        if (error is not null) return (false, error, false);

        // Nội dung y/c = noi_dung kéo về, kèm 1 dòng thông tin bộ phận / nhân viên / lập trình ở cuối.
        static string BuildContent(FsgProjectLookupService.RequestRow r)
        {
            var info = $"Bộ phận: {r.BpLt} | Nhân viên: {r.MaNv1} | Lập trình: {r.MaLt1}" + (string.IsNullOrWhiteSpace(r.MaXlyc) ? "" : " | Trạng thái y/c: " + r.MaXlyc.Trim());
            // Thứ tự: Menu (ten_menu) → noi_dung → Ghi chú (ghi_chu) → dòng thông tin bộ phận / nhân viên / lập trình.
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.TenMenu)) parts.Add("Menu: " + r.TenMenu.Trim());
            if (!string.IsNullOrWhiteSpace(r.NoiDung)) parts.Add(r.NoiDung.Trim());
            if (!string.IsNullOrWhiteSpace(r.GhiChu)) parts.Add("Ghi chú: " + r.GhiChu.Trim());
            return parts.Count == 0 ? info : string.Join("\r\n\r\n", parts) + "\r\n\r\n---\r\n" + info;
        }

        // Nội dung chỉ gồm phần tự sinh (Menu: / dòng "Bộ phận: …" / gạch ngang) — chưa có nội dung người dùng viết.
        static bool IsAutoOnly(string c) =>
            c.Length == 0 || c.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l != "---")
                .All(l => l.StartsWith("Bộ phận:", StringComparison.Ordinal) || l.StartsWith("Menu:", StringComparison.Ordinal));

        var byName = requests.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        int added = 0, skipped = 0, filled = 0;
        foreach (var row in rows)
        {
            if (byName.TryGetValue(row.Fcode1, out var existingReq))
            {
                skipped++;
                // y/c đã có: CHỈ cập nhật lại ô nội dung (Content) từ hệ thống; mọi thứ khác (Gen All, đường dẫn file, SQL Object, table, Top/Bottom script,
                // lịch sử Generate, Done...) giữ nguyên. Phần người dùng tự viết thêm SAU dòng "---/Bộ phận: …" cũng được giữ lại; nội dung tự viết không theo
                // khuôn tự sinh (không có dòng "---/Bộ phận") thì không đụng tới.
                var c = (existingReq.Content ?? "").Trim();
                var fresh = BuildContent(row);
                string? updated = null;
                if (IsAutoOnly(c)) updated = fresh;
                else
                {
                    var mInfo = Regex.Match(c, @"(?:^|\r?\n)---\r?\nBộ phận:[^\r\n]*");
                    if (mInfo.Success)
                    {
                        var tail = c[(mInfo.Index + mInfo.Length)..].Trim();   // phần người dùng tự viết thêm sau dòng thông tin
                        updated = tail.Length > 0 ? fresh + "\r\n\r\n" + tail : fresh;
                    }
                }
                if (updated is not null && updated.Trim() != c)
                {
                    existingReq.Content = updated.TrimEnd();
                    filled++;
                }
                continue;
            }
            var req = new AdvanceRequest { Name = row.Fcode1, Content = BuildContent(row) };
            requests.Add(req);
            byName[row.Fcode1] = req;
            added++;
        }

        var message = rows.Count == 0
            ? $"Không có yêu cầu nào của \"{programmer}\" trong dự án {code}."
            : $"Đã thêm {added} y/c mới" + (skipped > 0 ? $", {skipped} y/c đã có" : "") + (filled > 0 ? $" (cập nhật lại nội dung cho {filled})" : "") + $" — tổng {rows.Count} yêu cầu của {programmer}.";
        return (true, message, added > 0 || filled > 0);
    }
}
