namespace Bcode.App.Services.Rpt.Builder;

/// <summary>
/// Dựng sẵn bố cục file Excel mẫu (<see cref="SheetLayout"/>) từ <see cref="ReportSpec"/> — cùng quy tắc với trình thiết kế "Create RPT &amp; XML" (rút từ các mẫu zrpt_* / rpt* của Fast):
/// 4 dòng tên đơn vị, tiêu đề, dòng "từ ngày … đến ngày …", header <c>?h_cột</c> ở dòng 9, dữ liệu <c>!2.cột</c> ở dòng 10, dòng tổng SUMIF theo systotal, khối chữ ký.
/// Báo cáo pivot: sheet "Main - Pivot" + sheet "Main" ẩn (xem <see cref="PivotXlsxWriter"/>). Ghi file bằng <see cref="Write"/>.
/// </summary>
public static class ReportExcelLayout
{
    private const double PxPerChar = 7;
    private static double Chars(int px) => Math.Round((px / PxPerChar + 0.43) * 100) / 100;

    public static string ColName(int c) { var s = ""; while (c > 0) { var m = (c - 1) % 26; s = (char)(65 + m) + s; c = (c - m - 1) / 26; } return s; }

    public static SheetLayout Build(ReportSpec spec) => spec.IsMatrix ? Matrix(spec) : Table(spec);

    public static void Write(ReportSpec spec, string xlsxPath)
    {
        var layout = Build(spec);
        var excel = new ExcelTemplateWriter();
        if (layout.Pivot is not null) new PivotXlsxWriter(excel).Write(layout, xlsxPath);
        else excel.Write(layout, xlsxPath);
    }

    // ---- ô, dòng ----
    private sealed class Sheet
    {
        public readonly SheetLayout L = new();
        public void Put(int r, int c, string v, string k, bool f = false, bool n = false) =>
            L.Cells.Add(new SheetCell { R = r, C = c, V = v, K = k, F = f, N = n });
        public void Merge(int r1, int c1, int r2, int c2) => L.Merges.Add(new[] { r1, c1, r2, c2 });
        public void RowH(int r, double h) => L.Rows.Add(new SheetRow { R = r, H = h });
        public void RowHide(int r) => L.Rows.Add(new SheetRow { R = r, Hidden = true });
    }

    private static string RangeExpr() => "#?h_tu_ngay + + !1.tu_ngay + + ?h_den_ngay + + !1.den_ngay";

    private static void Signature(Sheet s, int n)
    {
        if (n < 3) return;
        var a = Math.Max(1, (int)Math.Round(n * 4 / 13.0));
        var b = Math.Min(n - 1, a + Math.Max(1, (int)Math.Round(n * 5 / 13.0)));
        if (n > 20) { a = (int)Math.Ceiling(n / 3.0); b = Math.Min(n - 1, a + (int)Math.Floor((n - a) / 2.0)); }
        s.Put(13, b + 1, "?reportDate", "sub"); s.Merge(13, b + 1, 13, n);
        foreach (var (c1, c2, t, sg) in new[] { (1, a, "?preparedBy", "?signatureFullname"), (a + 1, b, "?chiefAccountant", "?signatureFullname"), (b + 1, n, "?director", "?signatureFullnameSeal") })
        {
            s.Put(14, c1, t, "sig"); s.Merge(14, c1, 14, c2);
            s.Put(15, c1, sg, "sigName"); s.Merge(15, c1, 15, c2);
        }
        s.RowH(16, 60);
        s.Put(17, 1, "?preparedByName", "sig"); s.Merge(17, 1, 17, a);
        s.Put(17, a + 1, "?chiefAccountantName", "sig"); s.Merge(17, a + 1, 17, b);
        s.Put(17, b + 1, "?directorName", "sig"); s.Merge(17, b + 1, 17, n);
    }

    // =================================================================================================================
    //  Báo cáo thường
    // =================================================================================================================

    private sealed record F(string Name, bool IsNum, bool IsDate, bool Hidden, int Width, bool Sum);

    public static SheetLayout Table(ReportSpec spec)
    {
        var fields = new List<F>();
        if (spec.Stt) fields.Add(new("stt", true, false, false, 60, false));
        foreach (var c in spec.Columns)
            fields.Add(new(c.Name, c.Type is "Decimal" or "Int", c.Type == "DateTime", c.Hidden, c.Width > 0 ? c.Width : 100, (c.IsMeasure && c.Aggregate == "Sum") || !string.IsNullOrEmpty(c.Bal)));
        fields.Add(new("systotal", true, false, true, 60, false));            // cột ẩn làm mốc SUMIF (procedure luôn trả systotal)
        var n = Math.Max(fields.Count, 1);
        var s = new Sheet();
        s.L.ColWidths = Enumerable.Range(0, Math.Max(n, 6)).Select(i => i < fields.Count ? Chars(fields[i].Width) : 14.7).ToList();
        s.L.ColHidden = Enumerable.Range(0, Math.Max(n, 6)).Select(i => i < fields.Count && fields[i].Hidden).ToList();

        for (var i = 1; i <= 4; i++) s.Put(i, 1, "?Entity_Line" + i, i == 1 ? "entity1" : "plain");
        s.Put(6, 1, "?title", "title"); s.Merge(6, 1, 6, n);
        if (spec.DateRange) { s.Put(7, 1, RangeExpr(), "sub"); s.Merge(7, 1, 7, n); }
        s.RowH(9, 30);
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            if (f.Name != "systotal") s.Put(9, i + 1, "?h_" + f.Name, f.IsNum ? "hdrN" : "hdr");   // cột ẩn systotal không có tiêu đề
            s.Put(10, i + 1, "!2." + f.Name, f.IsDate ? "dataD" : f.IsNum ? "dataN" : "data");
        }
        s.RowHide(11);
        var sysIdx = fields.FindIndex(f => f.Name == "systotal");
        var hasAgg = fields.Any(f => f.Sum);
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            if (!f.IsNum || f.Name is "systotal" or "stt" || (hasAgg && !f.Sum)) continue;
            var col = ColName(i + 1); var sc = ColName(sysIdx + 1);
            s.Put(12, i + 1, $"=SUMIF(${sc}10:${sc}11,1,{col}10:{col}11)", "total", f: true);
        }
        Signature(s, n);
        s.L.Landscape = fields.Count(f => !f.Hidden) > 8;
        s.L.BoldComment = true;
        s.L.DataRow = 10;
        return s.L;
    }

    // =================================================================================================================
    //  Báo cáo pivot
    // =================================================================================================================

    private sealed class PF { public string Col = ""; public string Role = ""; public string Expr = ""; public string Name = ""; public bool IsNum, IsDate; public string DataName = ""; public int Width = 100; }

    public static SheetLayout Matrix(ReportSpec spec)
    {
        var m = spec.Matrix!;
        const int T = 9;
        var fm = new List<PF>();
        PF Add(string col, string role, bool num, bool date, int width)
        {
            var f = new PF { Col = col, Role = role, Expr = "!2." + col, Name = role is "row" or "colheader" ? "?h_" + col : col, IsNum = num, IsDate = date, Width = width };
            fm.Add(f); return f;
        }
        Add("xRow", "rowkey", false, false, 100);
        Add("xColumn", "colkey", false, false, 100);
        Add("xHeader", "colheader", false, false, 100);
        foreach (var r in m.Rows) Add(r.Name, "row", r.Type is "Decimal" or "Int", r.Type == "DateTime", r.Width);
        foreach (var v in m.Values) Add(v.Name, "data", true, false, v.Width);

        var rowsF = fm.Where(f => f.Role is "row" or "rowkey").ToList();
        var colsF = fm.Where(f => f.Role is "colkey" or "colheader").ToList();
        var dataF = fm.Where(f => f.Role == "data").ToList();
        int nR = rowsF.Count, K = colsF.Count, M = dataF.Count; var multi = M > 1; var nCf = K + (multi ? 1 : 0);
        var W = nR + Math.Max(nCf, M); var Hr = T + nCf; var Dr = Hr + 1;
        foreach (var f in dataF) f.DataName = multi ? "?p_" + f.Col : "Sum of " + f.Col;
        foreach (var f in colsF.Where(f => f.Role == "colkey")) f.DataName = "   ";

        const int off = 0;
        var nCols = Math.Max(off + W, 6);
        var s = new Sheet();
        int P(int k) => off + k;
        var c0 = P(Math.Max(1, rowsF.FindIndex(f => f.Role != "rowkey") + 1));
        for (var i = 1; i <= 4; i++) s.Put(i, c0, "?Entity_Line" + i, i == 1 ? "entity1" : "plain");
        s.Put(6, c0, "?title", "title"); s.Merge(6, c0, 6, P(W));
        if (spec.DateRange) { s.Put(7, c0, RangeExpr(), "sub"); s.Merge(7, c0, 7, P(W)); }

        s.Put(T, P(1), multi ? "" : dataF[0].DataName, "hdr");
        for (var i = 0; i < nCf; i++) s.Put(T, P(nR + 1 + i), i < K ? (colsF[i].Role == "colkey" ? colsF[i].DataName : colsF[i].Name) : " ", "hdr");
        for (var i = 1; i < nCf; i++) s.Put(T + i, P(nR + 1), colsF[i - 1].Expr, "hdr");
        for (var j = 0; j < rowsF.Count; j++)
        {
            var f = rowsF[j];
            s.Put(Hr, P(j + 1), f.Name, "hdr");
            s.Put(Dr, P(j + 1), f.Expr, f.Role == "row" && f.IsDate ? "dataD" : f.Role == "row" && f.IsNum ? "dataN" : "data");
        }
        if (!multi) s.Put(Hr, P(nR + 1), colsF[K - 1].Expr, "hdr");
        else for (var mi = 0; mi < dataF.Count; mi++) s.Put(Hr, P(nR + 1 + mi), dataF[mi].DataName, "hdr");
        for (var mi = 0; mi < (multi ? M : 1); mi++) s.Put(Dr, P(nR + 1 + mi), "0", "dataN", n: true);
        if (colsF.Count > 0 && colsF[0].Role == "colkey" && nCf >= 2) s.RowHide(T + 1);

        s.L.ColWidths = new List<double>(); s.L.ColHidden = new List<bool>();
        for (var c = 0; c < nCols; c++)
        {
            double w = 14.7; var hide = false;
            var rj = c - off;
            if (rj >= 0 && rj < nR) { var f = rowsF[rj]; w = f.Role == "rowkey" ? 7.4 : Chars(f.Width); hide = f.Role == "rowkey"; }
            else if (rj >= nR) w = Chars(dataF.Count > 0 ? Math.Max(dataF[0].Width, 120) : 120);
            s.L.ColWidths.Add(w); s.L.ColHidden.Add(hide);
        }
        s.RowH(Hr, 30);
        s.L.DataRow = Dr;
        s.L.BoldComment = false;
        s.L.Landscape = nCols > 8;
        s.L.Pivot = new PivotSpec
        {
            Fields = fm.Select(f => new PivotField { Name = f.Name, Expr = f.Expr, Role = f.Role, DataName = f.DataName, IsDate = f.IsDate }).ToList(),
            TopRow = T, LocationRef = $"{ColName(P(1))}{T}:{ColName(P(W))}{Dr}", FirstDataRow = nCf + 1, FirstDataCol = nR, DataCaption = " ",
        };
        return s.L;
    }
}
