using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bcode.App.Services.ExcelToRpt;

/// <summary>Kết quả phân tích một file Excel mẫu in: layout.json + nhật ký (như stdout của bản Python).</summary>
internal sealed record ExcelLayoutResult(JsonObject Layout, string Log);

/// <summary>
/// Excel mẫu in -> layout.json cho RptGenerator (port 1-1 của excel2rpt_parser.py, hàm convert()).
///
/// Mọi quyết định bố cục (vùng in, co giãn, nới ô, chia section, khử viền trùng...) giữ đúng
/// thứ tự và quy tắc của bản Python — layout.json là hợp đồng với RptGenerator.exe, và
/// bộ golden test (tests/Bcode.ExcelToRpt.Tests) so từng file với đầu ra của bản Python.
/// </summary>
internal sealed class ExcelLayoutParser
{
    private static readonly Dictionary<string, string> ExcelLineStyle = new()
    {
        ["hair"] = "dot", ["dotted"] = "dot",
        ["dashed"] = "dash", ["mediumDashed"] = "dash",
        ["dashDot"] = "dash", ["mediumDashDot"] = "dash",
        ["dashDotDot"] = "dash", ["mediumDashDotDot"] = "dash",
        ["slantDashDot"] = "dash",
        ["double"] = "double",
        ["thin"] = "solid", ["medium"] = "solid", ["thick"] = "solid",
    };

    private static readonly Dictionary<int, string> Paper = new()
    {
        [1] = "Letter", [8] = "A3", [9] = "A4", [11] = "A5", [5] = "Legal",
    };

    private static readonly Dictionary<string, (int w, int h)> SizeTwips = new()
    {
        ["Letter"] = (12240, 15840), ["A3"] = (16838, 23814), ["A4"] = (11906, 16838),
        ["A5"] = (8391, 11906), ["Legal"] = (12240, 20160),
    };

    private readonly XlsxSheet _ws;
    private readonly List<string> _log = new();

    private ExcelLayoutParser(XlsxSheet ws) => _ws = ws;

    private void Print(string line) => _log.Add(line);

    public static ExcelLayoutResult Convert(string xlsxPath)
    {
        var pkg = XlsxPackage.Load(xlsxPath);
        var parser = new ExcelLayoutParser(pkg.Sheet);
        var layout = parser.Run(xlsxPath, ExcelStyle.ThemePalette(pkg.ThemeXml));
        return new ExcelLayoutResult(layout, string.Join("\n", parser._log) + "\n");
    }

    public static string ConvertToJson(string xlsxPath) =>
        Convert(xlsxPath).Layout.ToJsonString(LayoutJson.Options);

    // -----------------------------------------------------------------------
    // Đơn vị: Excel -> twips (1/1440 inch)
    // -----------------------------------------------------------------------

    /// <summary>Math.Round mặc định của .NET là ToEven — khớp round() của Python.</summary>
    public static int ColWidthToTwips(double? width, double defaultWidth = 8.43) =>
        (int)Math.Round((width ?? defaultWidth) * 7 + 5) * 15;

    public static int RowHeightToTwips(double? height, double defaultHeight = 15.0) =>
        (int)Math.Round((height ?? defaultHeight) * 20);

    /// <summary>round(x, 1) của Python: làm tròn ToEven trên giá trị NHỊ PHÂN chính xác.</summary>
    public static double PyRound1(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return x;
        var bits = BitConverter.DoubleToInt64Bits(x);
        var neg = bits < 0;
        var exp = (int)((bits >> 52) & 0x7FF);
        var mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp++; else mant |= 1L << 52;
        exp -= 1075;
        // |x| * 10 = mant * 10 * 2^exp
        BigInteger num = new BigInteger(mant) * 10, den = BigInteger.One;
        if (exp >= 0) num <<= exp; else den <<= -exp;
        var q = BigInteger.DivRem(num, den, out var rem);
        var twice = rem * 2;
        if (twice > den || (twice == den && !q.IsEven)) q += 1;
        var result = (double)q / 10.0;
        return neg ? -result : result;
    }

    // -----------------------------------------------------------------------
    // Quy cách, viền
    // -----------------------------------------------------------------------

    private static void StyleAttrs(LayoutItem item, XCell cell, string[] palette, bool empty = false)
    {
        item.Bg = ExcelStyle.FillColor(cell, palette);
        item.BorderColor = ExcelStyle.BorderColor(cell, palette);
        if (empty) return;
        item.Color = ExcelStyle.FontColor(cell, palette);
        item.Underline = ExcelStyle.Underline(cell);
        var (pat, hidden) = ExcelStyle.CrystalNumberFormat(cell.Style.NumberFormat);
        item.NumberFormat = pat;
        // ';;;' là cách Excel GIẤU giá trị mà vẫn giữ ô; bên Crystal tương ứng Suppress
        if (hidden) item.Suppress = true;
    }

    private static string? SideStyle(XCell cell, string side) => cell.Style.Border.Get(side)?.Style;

    private static bool AnySide(XCell cell) => LayoutItem.Sides.Any(s => SideStyle(cell, s) is not null);

    /// <summary>
    /// Viền của một ô (hoặc vùng gộp), có tính cả ô LÁNG GIỀNG: trong Excel đường kẻ giữa hai
    /// ô chỉ cần MỘT trong hai ô khai là hiện. Chỉ áp cho ô vốn đã có ít nhất một cạnh kẻ, để
    /// ô không kẻ khung không bị lây viền từ bảng bên cạnh.
    /// </summary>
    private (Dictionary<string, bool> on, Dictionary<string, string> styles) BorderOf(
        int r0, int c0, int r1, int c1, int cHi, int rHi)
    {
        List<XCell> Cells(int rr, int ca, int cb)
        {
            var list = new List<XCell>();
            if (rr < 1 || rr > rHi) return list;
            for (var cc = ca; cc <= cb; cc++)
                if (cc >= 1 && cc <= cHi) list.Add(_ws.Cell(rr, cc));
            return list;
        }
        List<XCell> CellsV(int cc, int ra, int rb)
        {
            var list = new List<XCell>();
            if (cc < 1 || cc > cHi) return list;
            for (var rr = ra; rr <= rb; rr++)
                if (rr >= 1 && rr <= rHi) list.Add(_ws.Cell(rr, cc));
            return list;
        }
        static string? StyleOf(List<XCell> cs, string side)
        {
            foreach (var x in cs)
                if (SideStyle(x, side) is { } st) return st;
            return null;
        }

        var own = new Dictionary<string, bool>
        {
            ["top"] = StyleOf(Cells(r0, c0, c1), "top") is not null,
            ["bottom"] = StyleOf(Cells(r1, c0, c1), "bottom") is not null,
            ["left"] = StyleOf(CellsV(c0, r0, r1), "left") is not null,
            ["right"] = StyleOf(CellsV(c1, r0, r1), "right") is not null,
        };
        if (!own.Values.Any(v => v)) return (own, new Dictionary<string, string>());

        var raw = new Dictionary<string, string?>
        {
            ["top"] = StyleOf(Cells(r0, c0, c1), "top") ?? StyleOf(Cells(r0 - 1, c0, c1), "bottom"),
            ["bottom"] = StyleOf(Cells(r1, c0, c1), "bottom") ?? StyleOf(Cells(r1 + 1, c0, c1), "top"),
            ["left"] = StyleOf(CellsV(c0, r0, r1), "left") ?? StyleOf(CellsV(c0 - 1, r0, r1), "right"),
            ["right"] = StyleOf(CellsV(c1, r0, r1), "right") ?? StyleOf(CellsV(c1 + 1, r0, r1), "left"),
        };
        var on = new Dictionary<string, bool>();
        foreach (var k in LayoutItem.Sides) on[k] = raw[k] is not null;

        // Kiểu nét từng cạnh — mẫu in hay dùng nét chấm làm dòng kẻ điền tay
        var styles = new Dictionary<string, string>();
        foreach (var k in LayoutItem.Sides)
            if (on[k] && raw[k] is { } v)
                styles[k] = ExcelLineStyle.GetValueOrDefault(v, "solid");
        return (on, styles);
    }

    /// <summary>Cột/dòng cuối THỰC SỰ dùng: có giá trị, có kẻ viền, hoặc nằm trong vùng gộp.</summary>
    private (int c, int r) UsedRange()
    {
        int lastC = 0, lastR = 0;
        for (var r = 1; r <= _ws.MaxRow; r++)
            for (var c = 1; c <= _ws.MaxColumn; c++)
            {
                var cell = _ws.Cell(r, c);
                if (cell.Value is null && !AnySide(cell)) continue;
                lastC = Math.Max(lastC, c);
                lastR = Math.Max(lastR, r);
            }
        foreach (var m in _ws.Merges)
        {
            lastC = Math.Max(lastC, m.MaxCol);
            lastR = Math.Max(lastR, m.MaxRow);
        }
        return (lastC != 0 ? lastC : _ws.MaxColumn, lastR != 0 ? lastR : _ws.MaxRow);
    }

    /// <summary>Vùng in ("'Main'!$A$1:$N$41") -> (cột đầu, dòng đầu, cột cuối, dòng cuối).</summary>
    private (int c0, int r0, int c1, int r1)? ParsePrintArea()
    {
        if (string.IsNullOrEmpty(_ws.PrintArea)) return null;
        var m = Regex.Match(_ws.PrintArea, @"\$?([A-Z]{1,3})\$?(\d+)\s*:\s*\$?([A-Z]{1,3})\$?(\d+)");
        if (!m.Success) return null;
        static int ColNum(string letters) => letters.Aggregate(0, (n, ch) => n * 26 + (ch - 64));
        return (ColNum(m.Groups[1].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                ColNum(m.Groups[3].Value), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>'Rows to repeat at top' ('$10:$12') -> (10, 12): tín hiệu chắc nhất cho Page Header.</summary>
    private (int, int)? ParseTitleRows()
    {
        if (string.IsNullOrEmpty(_ws.PrintTitles)) return null;
        // Print_Titles có thể gồm cả cột lẫn dòng ('S'!$A:$B,'S'!$10:$12) — chỉ lấy phần dòng
        foreach (var part in _ws.PrintTitles.Split(','))
        {
            var refPart = part.Contains('!') ? part[(part.LastIndexOf('!') + 1)..] : part;
            var m = Regex.Match(refPart.Replace("$", ""), @"^\s*(\d+)\s*:\s*(\d+)\s*$");
            if (m.Success)
                return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Vùng bảng, dòng lặp, section
    // -----------------------------------------------------------------------

    /// <summary>
    /// Tìm vùng BẢNG: (1) Excel Table thật, (2) vùng kẻ khung đo theo BỀ RỘNG được kẻ, bung ra
    /// hai phía từ dòng chia nhiều cột nhất, bắt buộc liền số dòng Excel.
    /// </summary>
    private (int lo, int hi, string how)? FindTableBlock(SortedDictionary<int, List<LayoutItem>> rows)
    {
        foreach (var (name, reference) in _ws.Tables)
        {
            var ms = Regex.Matches(reference, @"[A-Z]+(\d+)");
            if (ms.Count >= 2)
                return (int.Parse(ms[0].Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(ms[1].Groups[1].Value, CultureInfo.InvariantCulture),
                        $"Excel Table '{name}' ({reference})");
        }

        static (HashSet<int> e, int n) Edges(List<LayoutItem> items)
        {
            var e = new HashSet<int>();
            var n = 0;
            foreach (var it in items)
            {
                if (!it.HasAnyBorder) continue;
                e.Add(it.X);
                e.Add(it.X + it.Width);
                n++;
            }
            return (e, n);
        }

        var info = rows.ToDictionary(kv => kv.Key, kv => Edges(kv.Value));
        if (info.Count == 0) return null;
        // Dòng MỐC: chia nhiều cột nhất (hoà thì dòng trên cùng)
        var refRow = info.Keys.OrderByDescending(r => info[r].n).ThenBy(r => r).First();
        if (info[refRow].n < 3) return null;
        var refE = info[refRow].e;

        const int tol = 30;
        var grid = new HashSet<int>(refE);
        int spanLo = refE.Min(), spanHi = refE.Max();

        static bool Near(int x, IEnumerable<int> pool) => pool.Any(y => Math.Abs(x - y) <= tol);

        bool Fits(int r)
        {
            var (e, n) = info[r];
            if (n < 2) return false;
            int a = e.Min(), b = e.Max();
            if (a < spanLo - tol || b > spanHi + tol) return false;
            if (e.All(x => Near(x, grid))) return true;
            return grid.Where(y => a - tol <= y && y <= b + tol).All(y => Near(y, e));
        }

        int lo = refRow, hi = refRow;
        while (rows.ContainsKey(lo - 1) && Fits(lo - 1))
        {
            lo--;
            grid.UnionWith(info[lo].e);
        }
        while (rows.ContainsKey(hi + 1) && Fits(hi + 1))
        {
            hi++;
            grid.UnionWith(info[hi].e);
        }
        return (lo, hi, $"vùng kẻ khung {grid.Count - 1} cột");
    }

    private static readonly Regex NumericRe = new(@"^[-+]?[\d.,\s]*\d[\d.,\s]*\z");

    /// <summary>
    /// Mẫu chưa đánh dấu: gộp các dòng dữ liệu LẶP LẠI (cùng tập cột có nội dung, liền nhau)
    /// thành một dòng. Chỉ gộp khi CHẮC: quá nửa số dòng mang số, hoặc dãy dài từ 4 dòng.
    /// </summary>
    private (int keep, int dropped)? CollapseRepeatRows(SortedDictionary<int, List<LayoutItem>> rows)
    {
        static double NumericShare(List<LayoutItem> items)
        {
            var vals = items.Where(it => it.Parsed.Kind == "text")
                            .Select(it => (it.Parsed.Text ?? "").Trim())
                            .Where(v => v.Length > 0).ToList();
            if (vals.Count == 0) return 0.0;
            return (double)vals.Count(v => NumericRe.IsMatch(v)) / vals.Count;
        }

        var groups = new List<(int[] sig, List<int> rs)>();
        foreach (var r in rows.Keys)
        {
            var sig = rows[r].Select(it => it.Col).OrderBy(c => c).ToArray();
            if (sig.Length == 0) continue;
            if (groups.Count > 0 && groups[^1].sig.SequenceEqual(sig) && groups[^1].rs[^1] + 1 == r)
                groups[^1].rs.Add(r);
            else
                groups.Add((sig, new List<int> { r }));
        }

        bool Solid(List<int> rs)
        {
            if (rs.Count < 2) return false;
            var nNum = rs.Count(r => NumericShare(rows[r]) > 0.5);
            return nNum * 2 > rs.Count || rs.Count >= 4;
        }

        var cands = groups.Where(g => Solid(g.rs)).ToList();
        if (cands.Count == 0) return null;

        var block = FindTableBlock(rows);
        bool Inside(List<int> rs) => block is { } b && b.lo <= rs[0] && rs[^1] <= b.hi;

        // Ưu tiên dãy trong vùng kẻ khung, rồi dãy dài hơn, rồi nhiều cột hơn (hoà: dãy đầu tiên)
        var best = cands[0];
        foreach (var g in cands.Skip(1))
        {
            var kb = (Inside(best.rs) ? 1 : 0, best.rs.Count, best.sig.Length);
            var kg = (Inside(g.rs) ? 1 : 0, g.rs.Count, g.sig.Length);
            if (kg.CompareTo(kb) > 0) best = g;
        }

        var keep = best.rs[0];
        foreach (var r in best.rs.Skip(1)) rows.Remove(r);
        return (keep, best.rs.Count - 1);
    }

    private static string PyList(IEnumerable<int> xs) => "[" + string.Join(", ", xs.OrderBy(x => x)) + "]";

    /// <summary>
    /// Suy luận section theo thứ tự ưu tiên: (1) print_title_rows -> Page Header, (2) điều kiện
    /// '#...&gt;' -> Detail, '#...=-9' -> Report Footer, (3) không có điều kiện: dòng nhiều trường
    /// nhất -> Detail, (4) còn lại: trên Detail -> Report Header, dưới -> Report Footer.
    /// </summary>
    private (Dictionary<int, string> sectionOf, List<string> notes) InferSections(
        SortedDictionary<int, List<LayoutItem>> rows, HashSet<int>? forcedDetail)
    {
        var detailRows = new HashSet<int>();
        var totalRows = new HashSet<int>();
        var nFields = new Dictionary<int, int>();
        if (forcedDetail is not null)
            detailRows.UnionWith(forcedDetail.Where(rows.ContainsKey));

        foreach (var (r, cells) in rows)
        {
            var n = 0;
            foreach (var c in cells)
            {
                var p = c.Parsed;
                if (p.Kind != "field") continue;
                n++;
                var cond = (p.Condition ?? "").Replace(" ", "");
                if (cond.Length == 0) continue;
                if (Regex.IsMatch(cond, @"=\s*-\d")) totalRows.Add(r);
                else if (cond.Contains('>') || cond.Contains('<')) detailRows.Add(r);
            }
            nFields[r] = n;
        }

        var notes = new List<string>();

        var block = FindTableBlock(rows);
        if (block is { } bl)
        {
            notes.Add($"Vung bang: dong {bl.lo}-{bl.hi} (nhan dien qua {bl.how})");
            var inBlock = rows.Keys.Where(r => bl.lo <= r && r <= bl.hi && nFields.GetValueOrDefault(r) != 0).ToList();
            if (inBlock.Count > 0)
            {
                var mx = inBlock.Max(r => nFields[r]);
                foreach (var r in inBlock)
                {
                    if (totalRows.Contains(r)) continue;
                    (nFields[r] == mx ? detailRows : totalRows).Add(r);
                }
            }
        }

        var title = ParseTitleRows();
        if (title is { } t0)
            notes.Add($"Page Header lay tu print_title_rows cua Excel (dong {t0.Item1}-{t0.Item2})");
        bool InTitle(int r) => title is { } t && t.Item1 <= r && r <= t.Item2;

        if (detailRows.Count == 0)
        {
            var best = nFields.Count > 0 ? nFields.Values.Max() : 0;
            if (best > 0)
            {
                detailRows = nFields.Where(kv => kv.Value == best && !InTitle(kv.Key)).Select(kv => kv.Key).ToHashSet();
                notes.Add($"Khong thay dieu kien loc dong -> doan Detail la dong nhieu truong nhat: {PyList(detailRows)}");
            }
        }

        int? firstDetail = detailRows.Count > 0 ? detailRows.Min() : null;
        var body = totalRows.Union(detailRows).ToList();
        int? lastBody = body.Count > 0 ? body.Max() : null;

        // Không có print_title_rows: đi ngược lên từ Detail, dòng liền kề có số ô tương đương
        // là tiêu đề cột -> Page Header; gặp dòng ít ô thì dừng -> Report Header
        var headerRows = new HashSet<int>();
        if (title is null && firstDetail is { } fd)
        {
            var need = Math.Max(2, rows[fd].Count / 2);
            var limit = block?.lo ?? 1;
            var r = fd - 1;
            while (rows.ContainsKey(r) && r >= limit && rows[r].Count >= need)
            {
                headerRows.Add(r);
                r--;
            }
            if (headerRows.Count > 0)
                notes.Add($"Khong co print_title_rows -> doan Page Header la dong {PyList(headerRows)} (nhieu o, nam ngay tren Detail)");
        }

        if (block is { } b2)
            foreach (var r in rows.Keys)
                if (b2.lo <= r && r <= b2.hi && nFields.GetValueOrDefault(r) == 0)
                    headerRows.Add(r);

        var sectionOf = new Dictionary<int, string>();
        foreach (var r in rows.Keys)
        {
            if (InTitle(r)) sectionOf[r] = "PageHeader";
            else if (detailRows.Contains(r)) sectionOf[r] = "Detail";
            else if (totalRows.Contains(r)) sectionOf[r] = "ReportFooter";
            else if (headerRows.Contains(r)) sectionOf[r] = "PageHeader";
            else if (firstDetail is { } f && r < f) sectionOf[r] = "ReportHeader";
            // Mọi thứ dưới phần thân giữ NGUYÊN THỨ TỰ trong Report Footer — Excel không có tín
            // hiệu nào cho biết dòng nào là chân trang, người dùng tự chuyển trong giao diện
            else if (lastBody is { } lb && r > lb) sectionOf[r] = "ReportFooter";
            else sectionOf[r] = "ReportHeader";
        }
        return (sectionOf, notes);
    }

    // -----------------------------------------------------------------------
    // convert()
    // -----------------------------------------------------------------------

    private sealed class SectionData
    {
        public List<(int excelRow, int height, List<LayoutItem> items, int y)> Rows = new();
        public int Height;
        public string? Condition;
        public bool HeightFirst;       // section tạo cho biến ẩn: thứ tự khoá rows, height, condition
    }

    private JsonObject Run(string xlsxPath, string[] palette)
    {
        var ws = _ws;
        var fileName = Path.GetFileName(xlsxPath);

        // --- Vùng in ---
        var area = ParsePrintArea();
        int cLo = 1, rLo = 1, cHi, rHi;
        if (area is { } a)
        {
            (cLo, rLo, cHi, rHi) = a;
            cHi = Math.Min(cHi, ws.MaxColumn);
            rHi = Math.Min(rHi, ws.MaxRow);
            Print($"  Vung in: {XlsxSheet.Coordinate(rLo, cLo)}:{XlsxSheet.Coordinate(rHi, cHi)} (bo qua o ngoai vung nay)");
        }
        else
        {
            (cHi, rHi) = UsedRange();
            if (cHi < ws.MaxColumn || rHi < ws.MaxRow)
                Print($"  Khong khai vung in -> dung vung thuc dung A1:{XlsxSheet.Coordinate(rHi, cHi)} " +
                      $"(bo {ws.MaxColumn - cHi} cot va {ws.MaxRow - rHi} dong trong o cuoi)");
        }

        // --- Tỉ lệ in của Excel: Crystal không có co giãn trang -> nhân thẳng vào toạ độ ---
        var pscale = (ws.Scale is { } sc && sc != 0 ? sc : 100) / 100.0;
        if (Math.Abs(pscale - 1.0) > 0.001)
            Print($"  Excel in o ti le {pscale.ToString("0%", CultureInfo.InvariantCulture)} -> ap dung vao toa do va co chu");

        // --- Toạ độ cột/dòng (twips, cộng dồn) ---
        var defaultW = ws.DefaultColWidth is { } dcw && dcw != 0 ? dcw : 8.43;
        var defaultH = ws.DefaultRowHeight != 0 ? ws.DefaultRowHeight : 15.0;

        var colX = new Dictionary<int, (int x, int w)>();
        var x = 0;
        for (var ci = cLo; ci <= cHi; ci++)
        {
            var has = ws.ColumnDims.TryGetValue(ci, out var dim);
            var w = has && dim.hidden ? 0
                : ColWidthToTwips(has && dim.width is { } dw && dw != 0 ? dw : null, defaultW);
            w = (int)Math.Round(w * pscale);
            colX[ci] = (x, w);
            x += w;
        }

        var rowY = new Dictionary<int, (int y, int h)>();
        var yAcc = 0;
        for (var ri = rLo; ri <= rHi; ri++)
        {
            var has = ws.RowDims.TryGetValue(ri, out var dim);
            var h = has && dim.hidden ? 0
                : RowHeightToTwips(has && dim.height is { } dh && dh != 0 ? dh : null, defaultH);
            h = (int)Math.Round(h * pscale);
            rowY[ri] = (yAcc, h);
            yAcc += h;
        }

        // --- Ô gộp ---
        var mergeOf = new Dictionary<(int, int), XMerge>();
        foreach (var m in ws.Merges) mergeOf[(m.MinRow, m.MinCol)] = m;
        var mergedMembers = new HashSet<(int, int)>();
        foreach (var m in ws.Merges)
            for (var r = m.MinRow; r <= m.MaxRow; r++)
                for (var c = m.MinCol; c <= m.MaxCol; c++)
                    if (r != m.MinRow || c != m.MinCol) mergedMembers.Add((r, c));

        int MergeW(XMerge m) => Enumerable.Range(m.MinCol, m.MaxCol - m.MinCol + 1).Where(colX.ContainsKey).Sum(c => colX[c].w);
        int MergeH(XMerge m) => Enumerable.Range(m.MinRow, m.MaxRow - m.MinRow + 1).Where(rowY.ContainsKey).Sum(r => rowY[r].h);

        // --- Duyệt ô ---
        var rows = new SortedDictionary<int, List<LayoutItem>>();
        var hiddenVars = new List<(string coord, string raw, ParsedCell parsed)>();
        var borderOnly = new List<(int r, int c, Dictionary<string, bool> bd, Dictionary<string, string> bst, LayoutItem sty)>();

        for (var r = 1; r <= ws.MaxRow; r++)
        {
            for (var c = 1; c <= ws.MaxColumn; c++)
            {
                if (mergedMembers.Contains((r, c))) continue;
                var cell = ws.Cell(r, c);
                if (cell.Value is null)
                {
                    // Ô trống có kẻ khung / tô nền vẫn phải vẽ, nếu không lưới bị hở, dải màu khuyết
                    if (cLo <= c && c <= cHi && rLo <= r && r <= rHi && rowY[r].h != 0 && colX[c].w != 0)
                    {
                        var (bd, bst) = BorderOf(r, c, r, c, cHi, rHi);
                        var sty = new LayoutItem();
                        StyleAttrs(sty, cell, palette, empty: true);
                        if (bd.Values.Any(v => v) || sty.Bg is not null)
                            borderOnly.Add((r, c, bd, bst, sty));
                    }
                    continue;
                }

                // Ô ngoài vùng in / dòng-cột ẩn: không in, nhưng biến (?nhãn, !trường) giữ lại
                // dưới dạng object ẨN để không mất biến
                var outside = !(cLo <= c && c <= cHi && rLo <= r && r <= rHi);
                var hidden = !outside && (rowY[r].h == 0 || colX[c].w == 0);
                var str = PyFormat.Str(cell.Value);
                if (outside || hidden)
                {
                    var v = str.Trim();
                    if (v.StartsWith('!') || v.StartsWith('?'))
                        hiddenVars.Add((XlsxSheet.Coordinate(r, c), v, CellSyntaxParser.ParseCellValue(str)));
                    continue;
                }
                var parsed = CellSyntaxParser.ParseCellValue(str);
                if (parsed.Kind == "skip") parsed.Cached = cell.Cached;

                int w, h;
                var hasMerge = mergeOf.TryGetValue((r, c), out var rng);
                if (hasMerge) { w = MergeW(rng!); h = MergeH(rng!); }
                else { w = colX[c].w; h = rowY[r].h; }

                var font = cell.Style.Font;
                var align = cell.Style.Alignment;
                var item = new LayoutItem
                {
                    Row = r, Col = c, X = colX[c].x, YInRow = 0, Width = w, Height = h,
                    Parsed = parsed,
                    Font = new ItemFont
                    {
                        Name = string.IsNullOrEmpty(font.Name) ? "Times New Roman" : font.Name,
                        Size = PyRound1((font.Size is { } fs && fs != 0 ? fs : 11) * pscale),
                        Bold = font.Bold,
                        Italic = font.Italic,
                    },
                    // Ưu tiên căn lề khai trong Excel; không có thì suy từ cờ %l / %r / %c
                    Align = !string.IsNullOrEmpty(align.Horizontal) ? align.Horizontal
                        : parsed.Format switch { "l" => "left", "r" => "right", "c" => "right", _ => "left" },
                    VAlign = !string.IsNullOrEmpty(align.Vertical) ? align.Vertical : "center",
                    Wrap = align.WrapText,
                };
                StyleAttrs(item, cell, palette);
                var (on, styles) = BorderOf(r, c, hasMerge ? rng!.MaxRow : r, hasMerge ? rng!.MaxCol : c, cHi, rHi);
                item.Border = on;
                if (styles.Count > 0) item.BorderStyles = styles;
                if (!rows.TryGetValue(r, out var list)) rows[r] = list = new List<LayoutItem>();
                list.Add(item);
            }
        }

        // --- Kiểm tra: có đúng là MẪU IN không? ---
        var nMarker = rows.Values.Sum(items => items.Count(it => it.Parsed.Kind is "label" or "field"));
        var nTotal = rows.Values.Sum(items => items.Count);
        var unmarked = nMarker == 0;
        if (unmarked)
        {
            // Không dừng: báo cáo ĐÃ XUẤT vẫn dùng làm khuôn được — giữ bố cục, khung bảng, khổ
            // giấy, và gộp dòng dữ liệu lặp lại thành một dòng Detail (CollapseRepeatRows)
            Print($"CHU Y: '{fileName}' khong co o danh dau ?nhan / !bang.truong.");
            Print($"       Da doc {nTotal} o - nhieu kha nang day la BAO CAO DA XUAT");
            Print("       chu khong phai mau in. Van dung duoc lam khuon:");
            Print("       tool giu bo cuc, khung bang, kho giay va gop cac dong du");
            Print("       lieu lap lai thanh mot dong Detail.");
            Print("       Gia tri trong dong Detail la SO LIEU MAU - mo giao dien,");
            Print("       chon tung o roi doi Kind sang Field de noi vao du lieu that.");

            // Ô công thức: lấy GIÁ TRỊ ĐÃ TÍNH làm chữ — đó là thứ hiện trên bản in
            var nCache = 0;
            foreach (var items in rows.Values)
                foreach (var it in items)
                {
                    var p = it.Parsed;
                    if (p.Kind != "skip" || p.Cached is null || (p.Cached is string cs && cs.Length == 0)) continue;
                    var txt = PyFormat.Str(p.Cached).Trim();
                    if (txt.Length == 0) continue;
                    it.Parsed = new ParsedCell { Kind = "text", Text = txt, Raw = txt };
                    nCache++;
                }
            if (nCache > 0)
                Print($"  Lay gia tri da tinh cua {nCache} o cong thuc lam chu " +
                      "(o mau chua danh dau thi day la noi dung that cua ban in)");
        }

        if (!unmarked && nMarker < nTotal * 0.1)
            Print($"CANH BAO: chi {nMarker}/{nTotal} o co danh dau ?/! - kiem tra lai xem co dung file mau in khong.");

        // Loại ô công thức: không đưa vào .rpt
        var nSkip = new Dictionary<string, int>();
        foreach (var r in rows.Keys.ToList())
        {
            var keep = new List<LayoutItem>();
            foreach (var it in rows[r])
            {
                if (it.Parsed.Kind == "skip")
                {
                    var why = it.Parsed.Reason ?? "?";
                    nSkip[why] = nSkip.GetValueOrDefault(why) + 1;
                }
                else keep.Add(it);
            }
            if (keep.Count > 0) rows[r] = keep;
            else rows.Remove(r);
        }
        foreach (var (why, n) in nSkip)
            Print($"  Da bo qua {n} o ({why}) - tu viet trong Crystal Designer");

        // --- Quy cách đọc thêm được ---
        var all = rows.Values.SelectMany(i => i).ToList();
        var stat = new (int n, string t)[]
        {
            (all.Count(i => i.Bg is not null), "o to nen"),
            (all.Count(i => i.Color is not null), "o mau chu"),
            (all.Count(i => i.NumberFormat is not null), "o dinh dang so/ngay"),
            (all.Count(i => i.BorderColor is not null), "o vien mau"),
            (all.Count(i => i.Underline), "o gach chan"),
            (all.Count(i => i.Suppress), "o an (;;;)"),
        };
        var msg = stat.Where(s => s.n > 0).Select(s => $"{s.n} {s.t}").ToList();
        if (msg.Count > 0) Print("  Quy cach: " + string.Join(", ", msg));

        // Chữ sáng màu trên nền trắng = in ra không thấy gì
        var pale = all.Count(it => it.Color is { } col && it.Bg is null &&
            new[] { 1, 3, 5 }.Min(i => System.Convert.ToInt32(col.Substring(i, 2), 16)) > 0xC0);
        if (pale > 0)
            Print($"  CANH BAO: {pale} o co chu rat sang ma khong co nen - in ra tren giay trang se khong doc duoc");

        // --- Nới rộng ô sang ô trống bên cạnh (Excel cho chữ dài tràn sang, Crystal cắt đúng mép) ---
        // Chỉ tràn sang ô vừa TRỐNG vừa KHÔNG KẺ VIỀN — nên không bao giờ tràn qua cột của bảng
        var occupied = new Dictionary<int, HashSet<int>>();
        HashSet<int> Occ(int r) => occupied.TryGetValue(r, out var s) ? s : occupied[r] = new HashSet<int>();
        foreach (var (r, items) in rows) Occ(r).UnionWith(items.Select(i => i.Col));
        foreach (var (mr, mc) in mergedMembers) Occ(mr).Add(mc);

        bool FreeCell(int r, int c)
        {
            if (c < cLo || c > cHi) return false;
            if (occupied.TryGetValue(r, out var s) && s.Contains(c)) return false;
            var cell = ws.Cell(r, c);
            if (cell.Value is not null) return false;
            return !AnySide(cell);
        }

        var nSpill = 0;
        foreach (var (r, items) in rows)
            foreach (var it in items)
            {
                if (it.Parsed.Kind is not ("text" or "label" or "field" or "concat")) continue;
                if (it.Wrap || it.HasAnyBorder) continue;
                var growR = it.Align is "left" or "center";
                var growL = it.Align is "right" or "center";

                var c = it.Col + 1;
                while (growR && FreeCell(r, c) && colX.ContainsKey(c))
                {
                    it.Width += colX[c].w;
                    Occ(r).Add(c);
                    nSpill++;
                    c++;
                }
                c = it.Col - 1;
                while (growL && FreeCell(r, c) && colX.ContainsKey(c))
                {
                    it.X -= colX[c].w;
                    it.Width += colX[c].w;
                    Occ(r).Add(c);
                    nSpill++;
                    c--;
                }
            }
        if (nSpill > 0) Print($"  Noi rong {nSpill} o sang o trong ben canh (nhu Excel cho tran chu)");

        // Mẫu chưa đánh dấu: gộp dòng lặp TRƯỚC khi chia section (dòng giữ lại là Detail)
        HashSet<int>? forcedDetail = null;
        if (unmarked)
        {
            if (CollapseRepeatRows(rows) is { } got)
            {
                forcedDetail = new HashSet<int> { got.keep };
                Print($"  Gop {got.dropped + 1} dong du lieu lap lai thanh 1 dong Detail (giu dong Excel {got.keep}, bo {got.dropped} dong)");
            }
            else
                Print("  Khong thay day dong du lieu lap lai nao - giu nguyen moi dong. Neu day la bao cao da xuat, " +
                      "hay tu xoa bot dong trong file Excel roi nap lai.");
        }

        var (sectionOf, notes) = InferSections(rows, forcedDetail);

        // --- Ô trống có kẻ khung: object rỗng để lưới không hở (chỉ trên dòng đã có nội dung) ---
        var nBd = 0;
        foreach (var (r, c, bd, bst, sty) in borderOnly)
        {
            if (!sectionOf.ContainsKey(r)) continue;
            var hasMerge = mergeOf.TryGetValue((r, c), out var rng);
            rows[r].Add(new LayoutItem
            {
                Row = r, Col = c, X = colX[c].x, YInRow = 0,
                Width = hasMerge ? MergeW(rng!) : colX[c].w,
                Height = hasMerge ? MergeH(rng!) : rowY[r].h,
                Parsed = new ParsedCell { Kind = "text", Text = "", Raw = "" },
                Font = new ItemFont { Name = "Times New Roman", Size = 9 },
                Align = "left", VAlign = "center", Wrap = false,
                Border = bd,
                BorderStyles = bst.Count > 0 ? bst : null,
                Bg = sty.Bg, BorderColor = sty.BorderColor,
            });
            nBd++;
        }
        if (nBd > 0) Print($"  Them {nBd} o vien rong de luoi khong bi ho");

        foreach (var n in notes) Print("  " + n);

        // --- Khử viền trùng TRONG CÙNG SECTION: cạnh chung do ô BÊN TRÁI / BÊN TRÊN vẽ ---
        // (Crystal vẽ viền bên trong hộp mỗi object, hai ô cùng vẽ cạnh chung là ra nét đôi.
        //  Khác section thì giữ: Detail có thể in 0 dòng, khử là dòng tổng mất nét trên.)
        var allItems = rows.SelectMany(kv => kv.Value.Select(it => (r: kv.Key, it))).ToList();
        var byRight = new Dictionary<(string, int), List<(int, int)>>();
        var byBottom = new Dictionary<(string, int), List<(int, int)>>();
        foreach (var (r, it) in allItems)
        {
            var y0 = rowY[r].y;
            var sec = sectionOf.GetValueOrDefault(r, "");
            if (it.BorderOn("right"))
                (byRight.TryGetValue((sec, it.X + it.Width), out var l1) ? l1 : byRight[(sec, it.X + it.Width)] = new()).Add((y0, y0 + it.Height));
            if (it.BorderOn("bottom"))
                (byBottom.TryGetValue((sec, y0 + it.Height), out var l2) ? l2 : byBottom[(sec, y0 + it.Height)] = new()).Add((it.X, it.X + it.Width));
        }

        var nDup = 0;
        foreach (var (r, it) in allItems)
        {
            int y0 = rowY[r].y, y1 = rowY[r].y + it.Height, x0 = it.X, x1 = it.X + it.Width;
            var sec = sectionOf.GetValueOrDefault(r, "");
            if (it.BorderOn("left") && byRight.TryGetValue((sec, x0), out var lr) &&
                lr.Any(p => Math.Min(y1, p.Item2) - Math.Max(y0, p.Item1) > 0))
            {
                it.Border["left"] = false;
                nDup++;
            }
            if (it.BorderOn("top") && byBottom.TryGetValue((sec, y0), out var lb) &&
                lb.Any(p => Math.Min(x1, p.Item2) - Math.Max(x0, p.Item1) > 0))
            {
                it.Border["top"] = false;
                nDup++;
            }
        }

        // Detail LẶP LẠI nên tự chạm chính nó: bỏ viền trên của dòng đầu Detail khi section trên
        // đã có nét đáy gánh thay (dò theo CAO ĐỘ TUYỆT ĐỐI, ô gộp dọc của header cũng tính)
        var det = rows.Keys.Where(r => sectionOf.GetValueOrDefault(r) == "Detail").ToList();
        var nRep = 0;
        if (det.Count > 0)
        {
            var topRow = det.Min();
            var topY = rowY[topRow].y;
            var covered = new HashSet<(int, int)>();
            foreach (var r in rows.Keys)
            {
                if (r >= topRow || sectionOf.GetValueOrDefault(r) is not ("PageHeader" or "ReportHeader")) continue;
                foreach (var it in rows[r])
                    if (it.BorderOn("bottom") && rowY[r].y + it.Height == topY)
                        covered.Add((it.X, it.X + it.Width));
            }
            foreach (var it in rows[topRow])
            {
                if (!it.BorderOn("top")) continue;
                int x0 = it.X, x1 = it.X + it.Width;
                if (covered.Any(p => Math.Min(x1, p.Item2) - Math.Max(x0, p.Item1) > 0))
                {
                    it.Border["top"] = false;
                    nRep++;
                }
            }
        }

        // Dọn kiểu nét của những cạnh vừa bị tắt
        foreach (var (_, it) in allItems)
        {
            if (it.BorderStyles is not { Count: > 0 } st) continue;
            var kept = st.Where(kv => it.BorderOn(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            it.BorderStyles = kept.Count > 0 ? kept : null;
        }

        if (nDup > 0) Print($"  Khu {nDup} canh vien trung trong cung section (tranh net doi)");
        if (nRep > 0) Print($"  Bo vien tren {nRep} o dong dau Detail (Detail lap lai, giu ca hai canh se ra net doi)");

        // Tách ô ghép SAU CÙNG: sau nới rộng (tính theo cột Excel), khử viền trùng và chia
        // section (đếm số trường mỗi dòng) — tách sớm là nhãn và trị đè lên nhau
        if (CellSyntaxParser.SplitConcatCells(rows) is { } split)
            Print($"  Tach {split.src} o ghep thanh {split.output} o rieng (nhan mot o, tri mot o - de keo tha va can le doc lap)");

        // --- Gom theo section ---
        var sectionOrder = new List<string>();
        var sections = new Dictionary<string, SectionData>();
        foreach (var (r, items) in rows)
        {
            var sec = sectionOf[r];
            if (!sections.TryGetValue(sec, out var sd))
            {
                sections[sec] = sd = new SectionData();
                sectionOrder.Add(sec);
            }
            sd.Rows.Add((r, rowY[r].h, items, 0));
        }

        // Điều kiện chung của cả section -> Suppress formula của section trong Crystal
        foreach (var sd in sections.Values)
        {
            var conds = sd.Rows.SelectMany(rw => rw.items).Where(it => it.Parsed.Kind == "field")
                          .Select(it => it.Parsed.Condition).Where(c => c is not null).Distinct().ToList();
            sd.Condition = conds.Count == 1 ? conds[0] : null;
        }

        foreach (var sd in sections.Values)
        {
            var first = sd.Rows[0].excelRow;
            var last = sd.Rows[^1].excelRow;
            var baseY = rowY[first].y;
            for (var i = 0; i < sd.Rows.Count; i++)
            {
                var rw = sd.Rows[i];
                var y = rowY[rw.excelRow].y - baseY;
                sd.Rows[i] = rw with { y = y };
                foreach (var it in rw.items) it.Y = y;
            }
            sd.Height = rowY[last].y + rowY[last].h - baseY;
        }

        // --- Danh sách trường theo bảng (để tạo data source) ---
        var tables = new Dictionary<string, SortedSet<string>>();
        void Collect(ParsedCell p)
        {
            if (p.Kind != "field") return;
            var key = p.Table.ToString(CultureInfo.InvariantCulture);
            if (!tables.TryGetValue(key, out var set)) tables[key] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(p.Name ?? "");
        }
        foreach (var items in rows.Values)
            foreach (var it in items) Collect(it.Parsed);

        // --- Biến ngoài vùng in / trong dòng-cột ẩn: object ẨN (không in) để KHÔNG MẤT BIẾN ---
        if (hiddenVars.Count > 0)
        {
            if (!sections.TryGetValue("ReportHeader", out var sd))
            {
                sections["ReportHeader"] = sd = new SectionData { HeightFirst = true };
                sectionOrder.Add("ReportHeader");
            }
            var hItems = new List<LayoutItem>();
            foreach (var (_, _, parsed) in hiddenVars)
            {
                if (parsed.Kind is not ("label" or "field")) continue;
                hItems.Add(new LayoutItem
                {
                    Row = 0, Col = 1, X = 0, Y = 0, OmitYInRow = true, Width = 900, Height = 240,
                    Parsed = parsed,
                    Font = new ItemFont { Name = "Times New Roman", Size = 8 },
                    Align = "left", VAlign = "center", Wrap = false,
                    Border = new Dictionary<string, bool>(), Suppress = true,
                });
                Collect(parsed);
            }
            if (hItems.Count > 0)
            {
                sd.Rows.Add((0, 0, hItems, 0));
                Print($"  Giu {hItems.Count} bien ngoai vung in duoi dang object AN (khong in): " +
                      string.Join(", ", hiddenVars.Select(v => v.raw)));
            }
        }

        // --- Khổ giấy: mã khổ giấy của Excel trùng chuẩn Windows DMPAPER_* ---
        var paperCode = ws.PaperSize is { } psz && psz != 0 ? psz : 9;
        if (!Paper.TryGetValue(paperCode, out var paper))
        {
            Print($"  Excel khong khai kho giay ro rang (ma {paperCode}) -> dung A4");
            paper = "A4";
        }
        var landscape = (string.IsNullOrEmpty(ws.Orientation) ? "portrait" : ws.Orientation) == "landscape";
        int mL = (int)Math.Round(ws.MarginLeft * 1440), mR = (int)Math.Round(ws.MarginRight * 1440),
            mT = (int)Math.Round(ws.MarginTop * 1440), mB = (int)Math.Round(ws.MarginBottom * 1440);

        var page = new JsonObject
        {
            ["paper"] = paper,
            ["paper_code"] = paperCode,
            // shrink = chỉ thu nhỏ khi tràn | fit = luôn vừa bề rộng | none = giữ nguyên
            ["fit"] = "shrink",
            // auto = cỡ chữ co cùng tỉ lệ | keep = giữ cỡ chữ gốc
            ["font_scale"] = "auto",
            // Cỡ chữ mặc định cho mọi ô (0 = theo Excel) — báo cáo hệ thống dùng thống nhất 10pt
            ["font_size"] = 10,
            ["title_font_size"] = 16,
            ["auto_row_height"] = true,
            // Nạp .xsd thì xoá sạch bảng cũ để DataSet của .rpt giống hệt .xsd
            ["xsd_replace_all"] = true,
            // lines = khung bảng bằng Line object liền mạch | borders = viền từng ô
            ["table_frame"] = "lines",
            ["landscape"] = landscape,
            ["margins"] = new JsonObject { ["left"] = mL, ["right"] = mR, ["top"] = mT, ["bottom"] = mB },
        };

        if (SizeTwips.TryGetValue(paper, out var size))
        {
            var (pw, ph) = size;
            if (landscape) (pw, ph) = (ph, pw);
            var usable = pw - mL - mR;
            page["usable_width"] = usable;
            Print($"  Kho giay: {paper} {(landscape ? "ngang" : "doc")}, be rong in duoc {usable} twips " +
                  $"({(usable / 1440.0).ToString("0.00", CultureInfo.InvariantCulture)} inch)");
            if (x > usable)
                Print($"  CANH BAO: bo cuc rong {x} twips > {usable} -> se co gian con " +
                      $"{((double)usable / x).ToString("0.0%", CultureInfo.InvariantCulture)} khi tao .rpt");
            else
                Print($"  Bo cuc rong {x} twips - vua kho giay ({((double)x / usable).ToString("0%", CultureInfo.InvariantCulture)})");

            // Mẫu quá rộng (hàng trăm cột): tỉ lệ dưới 35% thì không khổ chuẩn nào cứu được ->
            // chuyển sang khổ TUỲ CHỈNH đúng bằng bề rộng bố cục, giữ nguyên cỡ chữ
            if (x > usable / 0.35)
            {
                page["paper"] = "Custom";
                page["page_width"] = x + mL + mR;
                page["page_height"] = ph;
                page["fit"] = "none";
                page["usable_width"] = x;
                Print($"  Bo cuc rong gap {((double)x / usable).ToString("0", CultureInfo.InvariantCulture)} lan kho {paper} - khong kho chuan nao chua noi.");
                Print($"  => Tu chuyen sang kho TUY CHINH {((x + mL + mR) / 1440.0).ToString("0.0", CultureInfo.InvariantCulture)} x " +
                      $"{(ph / 1440.0).ToString("0.0", CultureInfo.InvariantCulture)} inch, giu nguyen co chu.");
                Print("     Xem tren man hinh / xuat PDF thi doc tot; in ra giay thuong thi KHONG vua.");
                Print("     Muon in giay thuong: bo bot cot trong file Excel goc, hoac doi 'Kho giay' o giao dien.");
            }
        }

        // --- Dựng JSON ---
        var tablesJson = new JsonObject();
        foreach (var (k, v) in tables) tablesJson[k] = new JsonArray(v.Select(n => (JsonNode?)n).ToArray());

        var sectionsJson = new JsonObject();
        foreach (var name in sectionOrder)
        {
            var sd = sections[name];
            var rowsJson = new JsonArray();
            foreach (var rw in sd.Rows)
                rowsJson.Add(new JsonObject
                {
                    ["excel_row"] = rw.excelRow,
                    ["height"] = rw.height,
                    ["items"] = new JsonArray(rw.items.Select(it => (JsonNode)it.ToJson()).ToArray()),
                    ["y"] = rw.y,
                });
            sectionsJson[name] = sd.HeightFirst
                ? new JsonObject { ["rows"] = rowsJson, ["height"] = sd.Height, ["condition"] = sd.Condition }
                : new JsonObject { ["rows"] = rowsJson, ["condition"] = sd.Condition, ["height"] = sd.Height };
        }

        var layout = new JsonObject
        {
            ["source"] = fileName,
            ["report_name"] = Path.GetFileNameWithoutExtension(xlsxPath),
            ["page"] = page,
            ["page_width_twips"] = x,
            ["tables"] = tablesJson,
            ["sections"] = sectionsJson,
        };

        var nItems = rows.Values.Sum(i => i.Count);
        Print($"OK: {nItems} đối tượng, {sections.Count} section");
        foreach (var name in new[] { "ReportHeader", "PageHeader", "Detail", "ReportFooter", "PageFooter" })
        {
            if (!sections.TryGetValue(name, out var sd)) continue;
            var n = sd.Rows.Sum(rw => rw.items.Count);
            Print($"    {name,-13} {n,3} ô   dòng Excel {"[" + string.Join(", ", sd.Rows.Select(rw => rw.excelRow)) + "]"}");
        }
        return layout;
    }
}
