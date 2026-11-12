using System.Globalization;
using System.Text;

namespace Bcode.Game.Data;

public sealed record Question(string Id, string Text, string[] Options, bool[] Correct, string Menu)
{
    public bool Multi => Correct.Count(c => c) > 1;
}

/// <summary>Một nghiệp vụ (Mã n.vụ) trong 1 phân hệ — đơn vị của 1 nhiệm vụ "viết tài liệu khảo sát".</summary>
public sealed record Business(string Code, string Name, List<Question> Questions);

public sealed record Module(string Sheet, List<Business> Businesses)
{
    public int QuestionCount => Businesses.Sum(b => b.Questions.Count);
}

/// <summary>Bộ câu hỏi trắc nghiệm đọc từ Excel do CHỦ PHÒNG chọn lúc mở phòng (không bao giờ có trong repo / bộ cài).
/// Mỗi sheet = 1 phân hệ (bỏ sheet không có cột "Câu hỏi", vd "Zalo chat trao đổi"). Cột dò theo TÊN ở dòng tiêu đề vì vị trí lệch giữa các sheet
/// (có sheet chèn "TT đặt câu hỏi", có sheet chỉ PA1..PA4). Đáp án 1 / 1.0 = đúng; 0, trống, " " = sai. Câu không có đáp án đúng nào thì bỏ.</summary>
public sealed class QuestionBank
{
    public string FilePath { get; }
    public List<Module> Modules { get; }
    public int Total => Modules.Sum(m => m.QuestionCount);
    public int MultiCount => Modules.Sum(m => m.Businesses.Sum(b => b.Questions.Count(q => q.Multi)));

    private QuestionBank(string path, List<Module> modules) { FilePath = path; Modules = modules; }

    public static QuestionBank Load(string path)
    {
        var sheets = XlsxReader.Read(path);
        var modules = new List<Module>();
        foreach (var sh in sheets)
        {
            if (sh.Rows.Count < 2) continue;
            var hdr = sh.Rows[0].Select(Norm).ToArray();
            int Col(string name) { var n = Norm(name); return Array.IndexOf(hdr, n); }
            var cq = Col("Câu hỏi");
            if (cq < 0) continue;
            var cCode = Col("Mã n.vụ");
            var cName = Col("Tên n.vụ");
            var cMenu = Col("Tên menu");
            var cId = Col("Mã câu hỏi");
            var pa = Enumerable.Range(1, 5).Select(i => Col("PA" + i)).ToArray();
            var da = Enumerable.Range(1, 5).Select(i => Col("ĐA" + i)).ToArray();
            var byCode = new Dictionary<string, Business>();
            var order = new List<Business>();
            string lastCode = "", lastName = "";
            for (var ri = 1; ri < sh.Rows.Count; ri++)
            {
                var row = sh.Rows[ri];
                string Cell(int c) => c >= 0 && c < row.Length ? (row[c] ?? "").Trim() : "";
                var text = Cell(cq);
                if (text.Length == 0) continue;
                var opts = new List<string>();
                var ok = new List<bool>();
                for (var i = 0; i < 5; i++)
                {
                    var o = Cell(pa[i]);
                    if (o.Length == 0) continue;
                    opts.Add(o);
                    ok.Add(IsOne(Cell(da[i])));
                }
                if (opts.Count < 2 || !ok.Contains(true)) continue;
                // Ô gộp / để trống mã nghiệp vụ = dùng mã dòng trên.
                var rawCode = Cell(cCode);
                var code = rawCode.Length > 0 ? rawCode : lastCode;
                var name = Cell(cName);
                if (name.Length == 0 && rawCode.Length == 0) name = lastName;
                lastCode = code; lastName = name;
                if (code.Length == 0) code = "(chung)";
                if (!byCode.TryGetValue(code, out var biz))
                {
                    biz = new Business(code, name.Length > 0 ? name : code, new List<Question>());
                    byCode[code] = biz;
                    order.Add(biz);
                }
                var id = Cell(cId);
                biz.Questions.Add(new Question(id.Length > 0 ? id : $"{sh.Name}#{ri + 1}", text, opts.ToArray(), ok.ToArray(), Cell(cMenu)));
            }
            if (order.Count > 0) modules.Add(new Module(sh.Name.Trim(), order));
        }
        if (modules.Count == 0) throw new InvalidDataException("Không thấy sheet nào có cột \"Câu hỏi\" + PA1/ĐA1 trong file này.");
        return new QuestionBank(path, modules);
    }

    private static bool IsOne(string v) =>
        v.Length > 0 && double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d - 1) < 1e-9;

    /// <summary>Chuẩn hoá tiêu đề: NFC, thường, bỏ khoảng trắng ("Mã N.Vụ" = "Mã n.vụ").</summary>
    private static string Norm(string? s) =>
        new string((s ?? "").Normalize(NormalizationForm.FormC).ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
}
