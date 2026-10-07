namespace Bcode.App.Services;

/// <summary>Một đoạn mẫu: gõ <see cref="Prefix"/> trong editor SQL rồi Tab / Enter để chèn <see cref="Body"/> (cú pháp snippet của Monaco: ${1:chữ mặc định}, $0 = vị trí con trỏ cuối).</summary>
public sealed record SqlSnippet(string Prefix, string Name, string Description, string Category, string Body);

/// <summary>Cách gọi quen thuộc của một procedure / function có sẵn (đã đếm từ code mẫu): chọn hàm đó trong gợi ý thì chèn nguyên dạng này với tham số điền sẵn.</summary>
public sealed record SqlCallIdiom(string RoutineName, string Insert, string Note);

/// <summary>
/// Danh mục mẫu cho gợi ý code SQL. Nguồn DUY NHẤT cho cả editor (đẩy sang trang) lẫn trang trợ giúp trong Settings — thêm / sửa mẫu ở đây là cả hai cùng cập nhật.
/// Quy ước: ký tự <c>¤</c> trong <c>Body</c> là dấu <c>$</c> thật (vì <c>$</c> là ký hiệu đặc biệt của snippet — tên như FastBusiness$Partition$Execute phải viết
/// FastBusiness¤Partition¤Execute); trang web đổi lại thành <c>\$</c> khi chèn. Các mẫu bám theo cách viết lặp lại nhiều nhất trong ~1.900 procedure của FBO
/// (số lần xuất hiện ghi ở mô tả).
/// </summary>
public static class SqlHintCatalog
{
    private const string UnitFn = "dbo.FastBusiness¤Function¤System¤GetUnitFilter";
    private const string CheckKeyFn = "dbo.FastBusiness¤Function¤System¤GetCheckKey";

    private static readonly SqlSnippet[] Base =
    {
        new("rsrep", "Procedure báo cáo chuẩn (rs_)", "Khung procedure báo cáo: tham số @DateFrom/@DateTo/@Unit/@Language/@UserID/@Admin, SET NOCOUNT/ANSI_NULLS, khối @UnitKey, #report với sysorder/sysprint/systotal.", "Khung",
            "CREATE PROCEDURE [dbo].[rs_${1:TenBaoCao}]\n\t@DateFrom SMALLDATETIME,\n\t@DateTo SMALLDATETIME,\n\t@Unit VARCHAR(1023),\n\t@Language CHAR(1),\n\t@UserID INT,\n\t@Admin BIT\nAS\nBEGIN\n\tSET NOCOUNT ON\n\tSET ANSI_NULLS OFF\n\n\tDECLARE @UnitKey NVARCHAR(1023)\n\tDECLARE @Key NVARCHAR(4000), @q NVARCHAR(4000)\n\n\tSET @UnitKey = " + UnitFn + "('ma_dvcs', @Unit, @UserID, @Admin)\n\n\tSELECT 5 AS sysorder, 1 AS sysprint, 1 AS systotal, ${2:*}\n\t\tINTO #report\n\t\tFROM ${3:bang} a\n\t\tWHERE $0\n\n\tSELECT * FROM #report\n\n\tSET NOCOUNT OFF\n\tSET ANSI_NULLS ON\nEND"),
        new("rsfn", "Function vô hướng (ff_)", "Khung function trả về một giá trị.", "Khung",
            "CREATE FUNCTION [dbo].[ff_${1:Ten}] (@${2:p} ${3:VARCHAR(32)})\nRETURNS ${4:VARCHAR(32)}\nAS\nBEGIN\n\tDECLARE @r ${4:VARCHAR(32)}\n\t$0\n\tRETURN @r\nEND"),
        new("unit", "Lọc theo đơn vị (@UnitKey)", "GetUnitFilter('ma_dvcs', @Unit, @UserID, @Admin) — dạng gọi xuất hiện 478 lần.", "Lọc dữ liệu",
            "DECLARE @UnitKey NVARCHAR(1023)\nSET @UnitKey = " + UnitFn + "('${1:ma_dvcs}', @Unit, @UserID, @Admin)"),
        new("key", "Dựng @Key + GetCheckKey", "Khởi tạo @Key rồi chuẩn hoá bằng GetCheckKey (662 lần).", "Lọc dữ liệu",
            "SET @Key = ' status = ''1'''\n$0\nSET @Key = " + CheckKeyFn + "(@Key)"),
        new("rsfilter", "Lọc theo tài khoản (GetAccountFilter)", "Khối IF @Account <> '' dựng điều kiện tài khoản vào @Key.", "Lọc dữ liệu",
            "IF @${1:Account} <> ''\n\tSET @Key = @Key + ' and ' + dbo.FastBusiness¤Function¤System¤GetAccountFilter('a.${2:tk}', 'like', @${1:Account})"),
        new("acckey", "Điều kiện tài khoản (GetAccountKey)", "EXEC GetAccountKey ... OUTPUT — dạng gọi hay gặp nhất (31 lần với a.tk_vt, 27 lần với a.tk).", "Lọc dữ liệu",
            "DECLARE @AccountKey NVARCHAR(4000)\nEXEC dbo.FastBusiness¤App¤GetAccountKey 'a.${1:tk}', 'like', @${2:Account}, '#¤dmtk0', @UserID, @Admin, @AccountKey OUTPUT"),
        new("sitekey", "Điều kiện kho / site (GetSiteFilter)", "EXEC GetSiteFilter 'a.ma_kho' ... OUTPUT (75 lần).", "Lọc dữ liệu",
            "DECLARE @SiteKey NVARCHAR(1023)\nEXEC FastBusiness¤System¤GetSiteFilter 'a.${1:ma_kho}', @Site, @UnitKey, @UserID, @Admin, @SiteKey OUTPUT"),
        new("part", "Truy vấn theo phân kỳ (Partition$Execute)", "Dựng @q với r00$%Partition rồi chạy Partition$Execute theo ngày — 518 procedure dùng.", "Lọc dữ liệu",
            "SET @q = 'select ${1:cot} from ${2:r00}¤%Partition with(nolock) where %[' + @Key + ']% group by ${3:cot}'\nEXEC FastBusiness¤Partition¤Execute @q, NULL, '${4:ngay_ct}', @DateFrom, @DateTo, @UserID, @Admin"),
        new("bil", "Tên song ngữ theo @Language", "CASE WHEN @Language = 'v' THEN ten_x ELSE ten_x2 END (334 lần; cặp hay gặp ten_kh, ten_vt, chi_tieu).", "Cột",
            "CASE WHEN @Language = 'v' THEN ${1:ten_vt} ELSE ${1:ten_vt}2 END AS ${1:ten_vt}"),
        new("sysrep", "Ba cột chuẩn của báo cáo", "sysorder / sysprint / systotal đầu danh sách cột (460 procedure).", "Cột",
            "SELECT ${1:5} AS sysorder, 1 AS sysprint, 1 AS systotal, $0"),
        new("round", "Lấy cấu hình làm tròn (options)", "SELECT @n = val FROM options WHERE name = 'm_round_…' — chọn tên bằng Tab (187 procedure).", "Cấu hình",
            "DECLARE @n${1:Round} TINYINT\nSELECT @n${1:Round} = val FROM options WHERE name = '${2|m_round_tien,m_round_tien_nt,m_round_gia,m_round_gia_nt,m_round_sl,m_round_tg|}'"),
        new("cycle", "Ngày đầu / cuối của kỳ", "ff_GetStartDateOfCycle / ff_GetEndDateOfCycle từ kỳ + năm (dạng gọi 48 lần).", "Ngày",
            "SELECT @${1:dFrom} = dbo.ff_GetStartDateOfCycle(@${2:Period}, @${3:Year}), @${4:dTo} = dbo.ff_GetEndDateOfCycle(@${2:Period}, @${3:Year})"),
        new("dyn", "SQL động (sp_executesql)", "Dựng chuỗi @q rồi EXEC sp_executesql (1.126 lần).", "Khung",
            "DECLARE @q NVARCHAR(4000)\nSET @q = '${1}'\nEXEC sp_executesql @q"),
        new("cur", "Vòng lặp cursor", "Khung DECLARE CURSOR … WHILE @@FETCH_STATUS = 0 (222 procedure dùng cursor).", "Khung",
            "DECLARE ${1:cr} CURSOR FOR\n\tSELECT ${2:cot} FROM ${3:bang}\nOPEN ${1:cr}\nFETCH NEXT FROM ${1:cr} INTO ${4:@bien}\nWHILE @@FETCH_STATUS = 0\nBEGIN\n\t$0\n\tFETCH NEXT FROM ${1:cr} INTO ${4:@bien}\nEND\nCLOSE ${1:cr}\nDEALLOCATE ${1:cr}"),
        new("balacc", "Số dư theo tài khoản", "Tạo bảng tạm đúng cột (SELECT TOP 0 … INTO … FROM cdtk) rồi INSERT … EXEC FastBusiness$Balance$Account. @BalanceType: 1 = số dư đầu (@DateFrom), 2 = số dư cuối (@DateTo) — suy ra từ cách gọi; @ResultType thường là 2.", "Số dư",
            "-- Cần: DECLARE @Join NVARCHAR(4000), @Key NVARCHAR(4000)\n-- Struct: bảng tạm chứa kết quả số dư (INSERT … EXEC cần bảng có sẵn đúng cột)\nSELECT TOP 0 tk, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt INTO #${1:b} FROM cdtk\nSELECT @Key = '${2:1 = 1}', @Join = ''\nSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\nINSERT INTO #${1:b} EXEC FastBusiness¤Balance¤Account @${3:DateFrom}, @Unit, @Account, ${4|1,2|}, 2, @UserID, @Admin, @Join, @Key"),
        new("balaccu", "Số dư theo tài khoản + đơn vị", "Tạo bảng tạm đúng cột (SELECT TOP 0 … INTO … FROM cdtk) rồi INSERT … EXEC FastBusiness$Balance$AccountUnit. @BalanceType: 1 = số dư đầu (@DateFrom), 2 = số dư cuối (@DateTo) — suy ra từ cách gọi; @ResultType thường là 2.", "Số dư",
            "-- Cần: DECLARE @Join NVARCHAR(4000), @Key NVARCHAR(4000)\n-- Struct: bảng tạm chứa kết quả số dư (INSERT … EXEC cần bảng có sẵn đúng cột)\nSELECT TOP 0 ma_dvcs, tk, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt INTO #${1:b} FROM cdtk\nSELECT @Key = '${2:1 = 1}', @Join = ''\nSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\nINSERT INTO #${1:b} EXEC FastBusiness¤Balance¤AccountUnit @${3:DateFrom}, @Unit, @Account, ${4|1,2|}, 2, @UserID, @Admin, @Join, @Key"),
        new("balcust", "Số dư theo khách hàng", "Tạo bảng tạm đúng cột (SELECT TOP 0 … INTO … FROM cdkh) rồi INSERT … EXEC FastBusiness$Balance$Customer. @BalanceType: 1 = số dư đầu (@DateFrom), 2 = số dư cuối (@DateTo) — suy ra từ cách gọi; @ResultType thường là 2.", "Số dư",
            "-- Cần: DECLARE @Join NVARCHAR(4000), @Key NVARCHAR(4000)\n-- Struct: bảng tạm chứa kết quả số dư (INSERT … EXEC cần bảng có sẵn đúng cột)\nSELECT TOP 0 tk, ma_kh, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt INTO #${1:b} FROM cdkh\nSELECT @Key = '${2:1 = 1}', @Join = ''\nSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\nINSERT INTO #${1:b} EXEC FastBusiness¤Balance¤Customer @${3:DateFrom}, @Unit, @Account, @Customer, ${4|1,2|}, 2, @UserID, @Admin, @Join, @Key"),
        new("balcustu", "Số dư theo khách hàng + đơn vị", "Tạo bảng tạm đúng cột (SELECT TOP 0 … INTO … FROM cdkh) rồi INSERT … EXEC FastBusiness$Balance$CustomerUnit. @BalanceType: 1 = số dư đầu (@DateFrom), 2 = số dư cuối (@DateTo) — suy ra từ cách gọi; @ResultType thường là 2.", "Số dư",
            "-- Cần: DECLARE @Join NVARCHAR(4000), @Key NVARCHAR(4000)\n-- Struct: bảng tạm chứa kết quả số dư (INSERT … EXEC cần bảng có sẵn đúng cột)\nSELECT TOP 0 ma_dvcs, tk, ma_kh, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt INTO #${1:b} FROM cdkh\nSELECT @Key = '${2:1 = 1}', @Join = ''\nSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\nINSERT INTO #${1:b} EXEC FastBusiness¤Balance¤CustomerUnit @${3:DateFrom}, @Unit, @Account, @Customer, ${4|1,2|}, 2, @UserID, @Admin, @Join, @Key"),
        new("baljob", "Số dư theo vụ việc", "Tạo bảng tạm đúng cột (SELECT TOP 0 … INTO … FROM cdvv) rồi INSERT … EXEC FastBusiness$Balance$Job. @BalanceType: 1 = số dư đầu (@DateFrom), 2 = số dư cuối (@DateTo) — suy ra từ cách gọi; @ResultType thường là 2.", "Số dư",
            "-- Cần: DECLARE @Join NVARCHAR(4000), @Key NVARCHAR(4000)\n-- Struct: bảng tạm chứa kết quả số dư (INSERT … EXEC cần bảng có sẵn đúng cột)\nSELECT TOP 0 tk, ma_vv, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt INTO #${1:b} FROM cdvv\nSELECT @Key = '${2:1 = 1}', @Join = ''\nSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\nINSERT INTO #${1:b} EXEC FastBusiness¤Balance¤Job @${3:DateFrom}, @Unit, @Account, @Job, ${4|1,2|}, 2, @UserID, @Admin, @Join, @Key"),
        new("balctc", "Số dư hợp đồng × khách hàng (_new)", "Tạo bảng tạm đúng cột (SELECT TOP 0 … INTO … FROM cdhd) rồi INSERT … EXEC FastBusiness$Balance$ContractCustomer_new. @BalanceType: 1 = số dư đầu (@DateFrom), 2 = số dư cuối (@DateTo) — suy ra từ cách gọi; @ResultType thường là 2.", "Số dư",
            "-- Cần: DECLARE @Join NVARCHAR(4000), @Key NVARCHAR(4000)\n-- Struct: bảng tạm chứa kết quả số dư (INSERT … EXEC cần bảng có sẵn đúng cột)\nSELECT TOP 0 tk, ma_hd, ma_kh, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt INTO #${1:b} FROM cdhd\nSELECT @Key = '${2:1 = 1}', @Join = ''\nSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\nINSERT INTO #${1:b} EXEC FastBusiness¤Balance¤ContractCustomer_new @${3:DateFrom}, @Unit, @Account, @Contract, @Customer, ${4|1,2|}, 2, @UserID, @Admin, @Join, @Key"),
        new("balfee", "Số dư khoản phí × khách hàng (_new)", "Tạo bảng tạm đúng cột (SELECT TOP 0 … INTO … FROM cdphi) rồi INSERT … EXEC FastBusiness$Balance$ExpenseCustomer_new. @BalanceType: 1 = số dư đầu (@DateFrom), 2 = số dư cuối (@DateTo) — suy ra từ cách gọi; @ResultType thường là 2.", "Số dư",
            "-- Cần: DECLARE @Join NVARCHAR(4000), @Key NVARCHAR(4000)\n-- Struct: bảng tạm chứa kết quả số dư (INSERT … EXEC cần bảng có sẵn đúng cột)\nSELECT TOP 0 tk, ma_phi, ma_kh, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt INTO #${1:b} FROM cdphi\nSELECT @Key = '${2:1 = 1}', @Join = ''\nSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\nINSERT INTO #${1:b} EXEC FastBusiness¤Balance¤ExpenseCustomer_new @${3:DateFrom}, @Unit, @Account, @Fee, @Customer, ${4|1,2|}, 2, @UserID, @Admin, @Join, @Key"),
        new("baljobc", "Số dư vụ việc × khách hàng (_new)", "Tạo bảng tạm đúng cột (SELECT TOP 0 … INTO … FROM cdvv) rồi INSERT … EXEC FastBusiness$Balance$JobCustomer_new. @BalanceType: 1 = số dư đầu (@DateFrom), 2 = số dư cuối (@DateTo) — suy ra từ cách gọi; @ResultType thường là 2.", "Số dư",
            "-- Cần: DECLARE @Join NVARCHAR(4000), @Key NVARCHAR(4000)\n-- Struct: bảng tạm chứa kết quả số dư (INSERT … EXEC cần bảng có sẵn đúng cột)\nSELECT TOP 0 tk, ma_vv, ma_kh, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt INTO #${1:b} FROM cdvv\nSELECT @Key = '${2:1 = 1}', @Join = ''\nSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\nINSERT INTO #${1:b} EXEC FastBusiness¤Balance¤JobCustomer_new @${3:DateFrom}, @Unit, @Account, @Job, @Customer, ${4|1,2|}, 2, @UserID, @Admin, @Join, @Key"),
        new("balcat", "Số dư đối tượng quản trị 3 × khách hàng (_new)", "Tạo bảng tạm đúng cột (SELECT TOP 0 … INTO … FROM zcdvvtd3) rồi INSERT … EXEC FastBusiness$Balance$CategoryCustomer_new. @BalanceType: 1 = số dư đầu (@DateFrom), 2 = số dư cuối (@DateTo) — suy ra từ cách gọi; @ResultType thường là 2.", "Số dư",
            "-- Cần: DECLARE @Join NVARCHAR(4000), @Key NVARCHAR(4000)\n-- Struct: bảng tạm chứa kết quả số dư (INSERT … EXEC cần bảng có sẵn đúng cột)\nSELECT TOP 0 tk, ma_td3 AS ma_dtqt, ma_kh, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt INTO #${1:b} FROM zcdvvtd3\nSELECT @Key = '${2:1 = 1}', @Join = ''\nSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\nINSERT INTO #${1:b} EXEC FastBusiness¤Balance¤CategoryCustomer_new @${3:DateFrom}, @Unit, @Account, @Mangement3, @Customer, ${4|1,2|}, 2, @UserID, @Admin, @Join, @Key"),
        new("balitem", "Số dư theo vật tư / kho", "Tạo bảng tạm đúng cột (SELECT TOP 0 … INTO … FROM cdvt) rồi INSERT … EXEC FastBusiness$Balance$Item. @BalanceType: 1 = số dư đầu, 2 = số dư cuối (suy ra từ cách gọi).", "Số dư",
            "-- Cần: DECLARE @Join NVARCHAR(4000), @Key NVARCHAR(4000)\n-- Struct: bảng tạm chứa kết quả số dư (INSERT … EXEC cần bảng có sẵn đúng cột)\nSELECT TOP 0 ma_kho, ma_vt, ton00 AS so_luong, du00 AS tien, du_nt00 AS tien_nt INTO #${1:b} FROM cdvt\nSELECT @Key = '${2:1 = 1}', @Join = ''\nSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\nINSERT INTO #${1:b} EXEC FastBusiness¤Balance¤Item @${3:DateFrom}, @Unit, @Site, @Item, ${4|1,2|}, 2, @DataType, @UserID, @Admin, @Join, @Key"),
        new("droptmp", "Xoá bảng tạm nếu có", "IF OBJECT_ID('tempdb..#t') IS NOT NULL DROP TABLE #t.", "Khung",
            "IF OBJECT_ID('tempdb..#${1:t}') IS NOT NULL DROP TABLE #${1:t}"),
    };

    /// <summary>Toàn bộ mẫu gõ tắt = nhóm cơ bản + khung procedure bổ sung + các mẫu điều kiện lọc.</summary>
    public static readonly SqlSnippet[] Snippets = Base.Concat(Extra()).ToArray();

    // ---- Điều kiện lọc: dạng nối @Key hay gặp nhất trong code mẫu (số trong mô tả = số procedure dùng) ----
    private static SqlSnippet LikeFilter(string prefix, string name, string variable, string field, int count) =>
        new(prefix, "Lọc " + name + " (LIKE tiền tố)", $"IF @{variable} <> '' → ' and {field} like ''…%''' — dạng lọc tiền tố; {count} procedure dùng.", "Điều kiện lọc",
            "IF @${1:" + variable + "} <> '' SET @Key = @Key + ' and ${2:" + field + "} like ''' + REPLACE(RTRIM(@${1:" + variable + "}), '''', '''''') + '%'''");

    private static SqlSnippet GroupFilter(string prefix, string name, string variable, string fieldBase, int count) =>
        new(prefix, "Lọc " + name + " (1/2/3)", $"Nhóm {name} cấp 1–3: chọn số bằng Tab ({fieldBase}1/2/3); {count}+ procedure dùng.", "Điều kiện lọc",
            "IF @" + variable + "${1|1,2,3|} <> '' SET @Key = @Key + ' and ${2:b." + fieldBase + "}${1} like ''' + REPLACE(RTRIM(@" + variable + "${1}), '''', '''''') + '%'''");

    private static IEnumerable<SqlSnippet> Extra()
    {
        // --- Khung procedure bổ sung ---
        yield return new("rsrepq", "Procedure báo cáo chuẩn đầy đủ (kỳ + phân kỳ)", "Khung đầy đủ hơn rsrep: @UnitKey, dựng @Key (gõ f… để thêm điều kiện lọc), lấy dữ liệu bằng Partition$Execute vào #report rồi SELECT.", "Khung",
            "CREATE PROCEDURE [dbo].[rs_${1:TenBaoCao}]\n\t@DateFrom SMALLDATETIME,\n\t@DateTo SMALLDATETIME,\n\t@Unit VARCHAR(1023),\n\t@Language CHAR(1),\n\t@UserID INT,\n\t@Admin BIT\nAS\nBEGIN\n\tSET NOCOUNT ON\n\tSET ANSI_NULLS OFF\n\n\tDECLARE @UnitKey NVARCHAR(1023), @Key NVARCHAR(4000), @q NVARCHAR(4000)\n\tSET @UnitKey = dbo.FastBusiness¤Function¤System¤GetUnitFilter('ma_dvcs', @Unit, @UserID, @Admin)\n\n\t-- Struct: cột kết quả\n\tSELECT TOP 0 5 AS sysorder, 1 AS sysprint, 1 AS systotal, ${2:cot} INTO #report FROM ${3:r00}¤000000\n\n\t-- @Key: điều kiện lọc (gõ fcust, fitem, fsite, fdept, flike… để thêm)\n\tSET @Key = ' status = ''1'''\n\tIF @UnitKey IS NOT NULL SET @Key = @Key + ' and ' + @UnitKey\n\t$0\n\tSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\n\n\t-- Dữ liệu theo kỳ\n\tSET @q = 'insert into #report select 5, 1, 1, ${2:cot} from ${3:r00}¤%Partition a with(nolock) where %[' + @Key + ']% group by ${2:cot}'\n\tEXEC FastBusiness¤Partition¤Execute @q, NULL, 'a.ngay_ct', @DateFrom, @DateTo, @UserID, @Admin\n\n\tSELECT * FROM #report ORDER BY sysorder\n\n\tSET NOCOUNT OFF\n\tSET ANSI_NULLS ON\nEND");
        yield return new("rspivot", "Procedure báo cáo PIVOT (khung có sẵn)", "Khung báo cáo pivot theo mẫu rs_rptExpensesAggregationReportPivot: dữ liệu thô vào #tmp (reportBy / pivotBy), #report có sysorder 5 (chi tiết) / 1 (tổng dòng) / 9 (in), SELECT kết quả, rồi bảng #pivot (id, name, header) mô tả các cột động ps$1, ps$2… — chỉ cần điền phần truy vấn chi tiết.", "Khung",
            "CREATE PROCEDURE [dbo].[rs_rpt${1:TenBaoCaoPivot}]\n\t@DateFrom SMALLDATETIME,\n\t@DateTo SMALLDATETIME,\n\t@ReportBy VARCHAR(64),\n\t@PivotBy VARCHAR(64),\n\t@Unit VARCHAR(1023),\n\t@Language CHAR(1),\n\t@UserID INT,\n\t@Admin BIT\nAS\nBEGIN\n\tSET NOCOUNT ON\n\tSET ANSI_NULLS OFF\n\n\tDECLARE @Key NVARCHAR(4000), @q NVARCHAR(4000), @UnitKey NVARCHAR(4000), @Join NVARCHAR(4000), @Total NVARCHAR(511)\n\tSELECT @Total = CASE WHEN @Language = 'V' THEN cname ELSE cname2 END FROM reports WHERE ccode = 'Total'\n\tSELECT @Key = '', @Join = ''\n\tSET @UnitKey = dbo.FastBusiness¤Function¤System¤GetUnitFilter('a.ma_dvcs', @Unit, @UserID, @Admin)\n\n\t-- @Key\n\tSELECT @Key = 'a.status = ''1'' and isnull(' + @ReportBy + ', '''') <> '''' and isnull(' + @PivotBy + ', '''') <> ''''', @Join = ''\n\tIF @UnitKey IS NOT NULL SET @Key = @Key + CASE WHEN @Key = '' THEN '' ELSE ' and ' END + @UnitKey\n\t$0\n\tSET @Key = dbo.FastBusiness¤Function¤System¤GetCheckKey(@Key)\n\n\t-- Dữ liệu thô\n\tSELECT TOP 0 ngay_ct, tk, CAST('' AS VARCHAR(33)) AS reportBy, CAST('' AS VARCHAR(33)) AS pivotBy, ps_no, ps_no_nt, ps_co, ps_co_nt INTO #tmp FROM ${2:r00}¤000000\n\tSET @q = 'insert into #tmp select ngay_ct, tk, rtrim(' + @ReportBy + '), rtrim(' + @PivotBy + '), sum(ps_no), sum(ps_no_nt), sum(ps_co), sum(ps_co_nt)'\n\tSET @q = @q + ' from ${2:r00}¤%Partition a with(nolock)' + @Join + ' where %[' + @Key + ']%'\n\tSET @q = @q + ' group by ngay_ct, tk, ' + @ReportBy + ', ' + @PivotBy\n\tEXEC FastBusiness¤Partition¤Execute @q, NULL, 'a.ngay_ct', @DateFrom, @DateTo, @UserID, @Admin\n\n\t-- Struct kết quả (sysorder: 5 = chi tiết, 1 = tổng dòng, 9 = dòng in tổng)\n\tSELECT TOP 0 5 AS sysorder, 1 AS sysprint, 1 AS systotal, CAST(0 AS INT) AS stt, CAST('' AS VARCHAR(33)) AS reportBy, CAST('' AS NVARCHAR(1024)) AS reportByName\n\t\t, CAST('' AS VARCHAR(33)) AS xCol, CAST('' AS NVARCHAR(1024)) AS xColName, CAST('' AS VARCHAR(1024)) AS xKey\n\t\t, ps_no AS ps, ps_no_nt AS ps_nt\n\t\tINTO #report FROM ${2:r00}¤000000\n\n\t-- Chi tiết: điền truy vấn insert into #report (sysorder = 5)\n\t-- INSERT INTO #report SELECT 5, 1, 1, 0, reportBy, reportBy, pivotBy, pivotBy, reportBy + space(1) + pivotBy, SUM(ps_no), SUM(ps_no_nt) FROM #tmp GROUP BY reportBy, pivotBy\n\n\t-- Tổng theo cột\n\tINSERT INTO #report (sysorder, sysprint, systotal, stt, reportBy, reportByName, xCol, xColName, xKey, ps, ps_nt)\n\t\tSELECT 1, 0, 0, 0, '', @Total, xCol, MAX(xColName), CHAR(10), SUM(ps), SUM(ps_nt) FROM #report WHERE sysorder = 5 GROUP BY xCol\n\tINSERT INTO #report (sysorder, sysprint, systotal, stt, reportBy, reportByName, xCol, xColName, xKey, ps, ps_nt)\n\t\tSELECT 9, 1, 0, 0, '', @Total, xCol, xColName, 'zzz', ps, ps_nt FROM #report WHERE sysorder = 1\n\n\t-- Kết quả\n\tSELECT sysorder, sysprint, systotal, stt, reportByName AS ten_ky, ps, ps_nt, reportBy AS xDetail, xCol, xColName, reportBy, xKey\n\t\tFROM #report ORDER BY xKey, sysorder, stt, xCol\n\n\t-- Pivot: danh sách cột động (ps¤1, ps¤2, … + cột tổng)\n\tCREATE TABLE #pivot(id INT IDENTITY(1, 1), name VARCHAR(64) NULL, header NVARCHAR(1024) NULL)\n\tINSERT INTO #pivot SELECT DISTINCT 'ps_nt¤', xCol FROM #report ORDER BY xCol\n\tUPDATE #pivot SET name = name + RTRIM(id)\n\tINSERT INTO #pivot SELECT 'ps_nt', @Total\n\tSELECT * FROM #pivot ORDER BY id\n\n\tSET NOCOUNT OFF\n\tSET ANSI_NULLS ON\nEND");

        // --- Điều kiện lọc tổng quát ---
        yield return new("flike", "Lọc LIKE tiền tố (tổng quát)", "IF @biến <> '' SET @Key = @Key + ' and a.cột like ''…%''' — gõ rồi điền tên biến và cột.", "Điều kiện lọc",
            "IF @${1:Variable} <> '' SET @Key = @Key + ' and ${2:a.ma_kh} like ''' + REPLACE(RTRIM(@${1:Variable}), '''', '''''') + '%'''");
        yield return new("feq", "Lọc bằng chính xác (=)", "IF @biến <> '' SET @Key = @Key + ' and a.cột = ''…''' (vd tiền tệ ma_nt, vị trí).", "Điều kiện lọc",
            "IF @${1:Currency} <> '' SET @Key = @Key + ' and ${2:a.ma_nt} = ''' + REPLACE(RTRIM(@${1:Currency}), '''', '''''') + ''''");
        yield return new("fin", "Lọc IN (danh sách chính xác, cách nhau dấu phẩy)", "a.cột IN ('A','B',…): bỏ dấu cách, nhân đôi dấu nháy, đổi dấu phẩy thành ','. Dạng của ma_gd, ma_ct.", "Điều kiện lọc",
            "IF @${1:TransactionCode} <> '' SET @Key = @Key + ' and ${2:a.ma_gd} in (''' + REPLACE(REPLACE(REPLACE(@${1:TransactionCode}, ' ', ''), '''', ''''''), ',', ''',''') + ''')'");
        yield return new("finlist", "Lọc theo danh sách TIỀN TỐ (ff_Inlist)", "dbo.ff_Inlist(cột, danh_sách) = 1: cột bắt đầu bằng MỘT trong các tiền tố cách nhau dấu phẩy (như LIKE 'x%' cho từng mục) — khác fin (khớp chính xác).", "Điều kiện lọc",
            "IF @${1:Customer} <> '' SET @Key = @Key + ' and dbo.ff_Inlist(${2:a.ma_kh}, ''' + REPLACE(@${1:Customer}, '''', '''''') + ''') = 1'");
        yield return new("frange", "Lọc từ … đến … (số chứng từ, ff_PadL)", "a.so_ct >= PadL(từ) và <= PadL(đến) — dạng của InvoiceFrom/InvoiceTo, inspectionFrom/To (~100 procedure).", "Điều kiện lọc",
            "IF @${1:InvoiceFrom} <> '' SET @Key = @Key + ' and ${3:a.so_ct} >= ''' + dbo.ff_PadL(REPLACE(RTRIM(@${1:InvoiceFrom}), '''', ''''''), @${4:Size}) + ''''\nIF @${2:InvoiceTo} <> '' SET @Key = @Key + ' and ${3:a.so_ct} <= ''' + dbo.ff_PadL(REPLACE(RTRIM(@${2:InvoiceTo}), '''', ''''''), @${4:Size}) + ''''");
        yield return new("fkey", "Gắn điều kiện đã dựng sẵn vào @Key", "IF @UnitKey IS NOT NULL SET @Key = @Key + ' and ' + @UnitKey (366 lần; cũng dùng cho @siteKey, @AccountKey).", "Điều kiện lọc",
            "IF @${1:UnitKey} IS NOT NULL SET @Key = @Key + ' and ' + @${1:UnitKey}");
        yield return new("fdel", "Bỏ chứng từ đã xoá (m_delete_log)", "Khi cấu hình m_delete_log = 1 thì thêm điều kiện a.status <> '*' (22 procedure).", "Điều kiện lọc",
            "IF EXISTS(SELECT 1 FROM options WHERE name = 'm_delete_log' AND val = '1') SET @Key = @Key + ' and (a.status <> ''*'')'");

        // --- Điều kiện lọc theo từng đối tượng (biến + cột hay gặp nhất, số procedure dùng) ---
        yield return LikeFilter("fcust", "khách hàng", "Customer", "a.ma_kh", 145);
        yield return LikeFilter("fitem", "vật tư", "Item", "a.ma_vt", 108);
        yield return LikeFilter("fsite", "kho", "Site", "a.ma_kho", 95);
        yield return LikeFilter("fdept", "bộ phận", "Department", "a.ma_bp", 74);
        yield return LikeFilter("fjob", "vụ việc", "Job", "a.ma_vv", 70);
        yield return LikeFilter("fcon", "hợp đồng", "Contract", "a.ma_hd", 45);
        yield return LikeFilter("freason", "lý do (nhập xuất)", "Reason", "a.ma_nx", 40);
        yield return LikeFilter("fitype", "loại vật tư", "ItemType", "a.loai_vt", 30);
        yield return LikeFilter("fprod", "sản phẩm", "Product", "a.ma_sp", 24);
        yield return LikeFilter("fsales", "nhân viên bán hàng", "Salesman", "a.ma_nvbh", 23);
        yield return LikeFilter("fcur", "ngoại tệ", "Currency", "a.ma_nt", 20);
        yield return LikeFilter("fmo", "lệnh sản xuất", "MO", "a.so_lsx", 16);
        yield return LikeFilter("flot", "lô", "Lot", "a.ma_lo", 12);
        yield return GroupFilter("fitemgrp", "nhóm vật tư", "ItemGroup", "nh_vt", 61);
        yield return GroupFilter("fcgrp", "nhóm khách hàng", "CustomerGroup", "nh_kh", 22);
        yield return new("ftrans", "Lọc mã giao dịch (IN)", "a.ma_gd IN (…) — 40 procedure.", "Điều kiện lọc",
            "IF @${1:TransactionCode} <> '' SET @Key = @Key + ' and a.ma_gd in (''' + REPLACE(REPLACE(REPLACE(@${1:TransactionCode}, ' ', ''), '''', ''''''), ',', ''',''') + ''')'");
        yield return new("fvc", "Lọc mã chứng từ (IN)", "a.ma_ct IN (…) — 11 procedure.", "Điều kiện lọc",
            "IF @${1:VoucherCode} <> '' SET @Key = @Key + ' and a.ma_ct in (''' + REPLACE(REPLACE(REPLACE(@${1:VoucherCode}, ' ', ''), '''', ''''''), ',', ''',''') + ''')'");
    }

    /// <summary>Mẫu "gõ tên + tên bảng → điền sẵn cột": hành vi nằm trong Web/Shell/sqlhints.js, đây là phần mô tả cho trang hướng dẫn.</summary>
    public static readonly (string Usage, string Example, string Text)[] SmartSnippets =
    {
        ("part <bảng>", "part #tmp   ·   part dmvt", "Truy vấn phân kỳ điền sẵn cột: với bảng tạm / biến bảng trong script (kể cả SELECT TOP 0 … INTO #t, CREATE TABLE #t, DECLARE @t TABLE) → insert into #t select <cột của #t> from r00¤%Partition …; với bảng trong database → insert into #tmp select <cột của bảng> from <bảng>¤%Partition …. Cột của #t lấy từ chính danh sách SELECT / CREATE TABLE trong script (alias, CAST … AS, SELECT * từ bảng tạm khác đều được xử lý)."),
        ("sel <bảng>", "sel #report   ·   sel dmkh", "SELECT mọi cột của bảng FROM bảng."),
        ("ins <bảng>", "ins #t", "INSERT INTO bảng (cột…) SELECT cột… FROM nguồn."),
        ("cur <bảng>", "cur #t", "Khai báo biến @cột cho từng cột (đổi kiểu bằng Tab), cursor, FETCH NEXT INTO @…, vòng WHILE, CLOSE / DEALLOCATE."),
        ("sysrep <bảng>", "sysrep #t", "SELECT sysorder, sysprint, systotal rồi mọi cột của bảng."),
        ("bil <bảng | cột>", "bil #c   ·   bil dmkh   ·   bil ten_kh", "Với bảng: tự tìm các cặp cột ten_x / ten_x2 (ten_*, chi_tieu, dien_giai, name…) và sinh CASE WHEN @Language = 'v' … cho từng cặp; với tên cột: sinh CASE cho đúng cột đó."),
        ("gõ \"tên mẫu \" (có dấu cách)", "part␣", "Gõ tên mẫu rồi dấu cách sẽ liệt kê các bảng tạm trong script để chọn."),
    };

    /// <summary>Cách gọi quen thuộc theo tên routine (chữ thường). Chọn routine trong danh sách gợi ý thì chèn đúng dạng này.</summary>
    public static readonly SqlCallIdiom[] Idioms =
    {
        new("fastbusiness$function$system$getcheckkey", CheckKeyFn + "(${1:@Key})", "Dạng gọi trong 600+ procedure"),
        new("fastbusiness$function$system$getunitfilter", UnitFn + "('${1:ma_dvcs}', @Unit, @UserID, @Admin)", "235 lần với 'ma_dvcs', 89 lần với 'a.ma_dvcs'"),
        new("fastbusiness$partition$execute", "FastBusiness¤Partition¤Execute @q, NULL, '${1:ngay_ct}', @DateFrom, @DateTo, @UserID, @Admin", "181 lần với 'a.ngay_ct', 113 lần với 'ngay_ct'"),
        new("fastbusiness$app$getaccountkey", "dbo.FastBusiness¤App¤GetAccountKey 'a.${1:tk}', 'like', @${2:Account}, '#¤dmtk0', @UserID, @Admin, @AccountKey OUTPUT", "Dạng hay gặp nhất"),
        new("fastbusiness$system$getsitefilter", "FastBusiness¤System¤GetSiteFilter 'a.${1:ma_kho}', @Site, @UnitKey, @UserID, @Admin, @SiteKey OUTPUT", "75 lần"),
        new("fastbusiness$system$updateorderfield", "FastBusiness¤System¤UpdateOrderField '', @${1:Order}, '#report', 'stt', ''", "26 lần"),
        new("fastbusiness$report$getdynamickey", "FastBusiness¤Report¤GetDynamicKey @${1:TableName}, @filterKey OUTPUT", "49 lần"),
        new("fastbusiness$function$system$split", "dbo.FastBusiness¤Function¤System¤Split(@${1:list}, ',')", "Tách chuỗi theo dấu phẩy"),
        new("ff_getstartdateofcycle", "dbo.ff_GetStartDateOfCycle(@${1:Period}, @${2:Year})", "Ngày đầu kỳ"),
        new("ff_getenddateofcycle", "dbo.ff_GetEndDateOfCycle(@${1:Period}, @${2:Year})", "Ngày cuối kỳ"),
        // ---- Bổ sung từ phân tích ~1.900 procedure của FBO: mỗi hàm dưới đây có một dạng gọi chiếm phần lớn số lần dùng (số lần ghi ở cột ghi chú) ----
        new("fastbusiness$function$system$getunitfilterex", "dbo.FastBusiness¤Function¤System¤GetUnitFilterEx('${1:a.ma_dvcs}', @Unit, @UserID, @Admin, '${2:New}')", "24/72 lần gọi — bản có thêm quyền (@Right)"),
        new("fastbusiness$system$getupdatevaluefields", "FastBusiness¤System¤GetUpdateValueFields '#${1:report}', @Controller, 1, @Language, @UserID, @Admin, @SysDB, @Statement OUTPUT", "81/108 lần gọi"),
        new("fastbusiness$report$getphysicskey", "FastBusiness¤Report¤GetPhysicsKey @${1:k} OUTPUT, @DataType, @Unit, @UserID, @Admin", "50/56 lần gọi"),
        new("fastbusiness$function$getfieldround", "dbo.FastBusiness¤Function¤GetFieldRound('${1:wrkin}', '${2:sl_nhap}')", "48/48 lần gọi: (bảng, cột) → số chữ số làm tròn"),
        new("rs_gettransactionkey", "rs_GetTransactionKey @VoucherCode, @TransactionList, 1, @Controller, @SysDB, '${1:a.}', @Key OUTPUT", "16/32 lần gọi"),
        new("fastbusiness$function$ext$insert", "dbo.FastBusiness¤Function¤Ext¤Insert('#${1:gl}', '#d${2:GL}', '#${1:gl}', '#d${2:GL}', NULL, NULL)", "46/112 lần gọi"),
        new("fastbusiness$app$voucher$approvalhistory", "FastBusiness¤App¤Voucher¤ApprovalHistory @idNumber, @masterTable, @HisTable, '${1:1}'", "13/36 lần gọi"),
        new("ff_getdatalength", "dbo.ff_GetDataLength('${1:so_ct}')", "25/41 lần gọi: độ dài dữ liệu của trường"),
        new("fastbusiness$function$getarisingdateandperiod", "dbo.FastBusiness¤Function¤GetArisingDateAndPeriod(@DateFrom, @DateTo)", "24/54 lần gọi (table-function)"),
        new("ff_inunitrights", "dbo.ff_InUnitRights(${1:ma_dvcs}, RTRIM(@${2:cUnit}), @${3:cUnits})", "20/49 lần gọi: kiểm tra đơn vị có trong quyền"),
        new("ff_isrelationjob", "dbo.ff_IsRelationJob(@${1:Job}, ${2:ma_vv})", "15/21 lần gọi"),
        new("fastbusiness$function$bi$getpurorgfilter", "dbo.FastBusiness¤Function¤BI¤GetPurOrgFilter('${1:a.ma_bp0}', @${2:PurOrg}, @UserID, @Admin)", "8/18 lần gọi"),
        new("fastbusiness$function$getwordcount", "dbo.FastBusiness¤Function¤GetWordCount(@${1:list}, ',')", "9/16 lần gọi: đếm số phần tử của chuỗi phân cách"),
        new("fastbusiness$app$getlayoutconfig", "FastBusiness¤App¤GetLayoutConfig @userID, '${1:number_format}', @sysDB, @${2:formatConfig} OUTPUT", "16/16 lần gọi"),
        // ---- Họ hàm số dư FastBusiness$Balance$*: luôn được gọi bằng INSERT #bảng EXEC …; 1 = số dư đầu (@DateFrom), 2 = số dư cuối (@DateTo) ----
        // ---- Họ hàm số dư FastBusiness$Balance$*: gọi bằng INSERT #bảng EXEC …; 1 = số dư đầu (@DateFrom), 2 = số dư cuối (@DateTo) — suy ra từ cách gọi ----
        new("fastbusiness$balance$account", "FastBusiness¤Balance¤Account @${1:DateFrom}, @Unit, @Account, ${2|1,2|}, 2, @UserID, @Admin, @Join, @Key", "dạng gọi chuẩn của họ hàm số dư; kết quả vào bảng tạm bằng INSERT #t EXEC"),
        new("fastbusiness$balance$accountunit", "FastBusiness¤Balance¤AccountUnit @${1:DateFrom}, @Unit, @Account, ${2|1,2|}, 2, @UserID, @Admin, @Join, @Key", "theo từng đơn vị"),
        new("fastbusiness$balance$customer", "FastBusiness¤Balance¤Customer @${1:DateFrom}, @Unit, @Account, @Customer, ${2|1,2|}, 2, @UserID, @Admin, @Join, @Key", "theo khách hàng"),
        new("fastbusiness$balance$customerunit", "FastBusiness¤Balance¤CustomerUnit @${1:DateFrom}, @Unit, @Account, @Customer, ${2|1,2|}, 2, @UserID, @Admin, @Join, @Key", "khách hàng theo đơn vị"),
        new("fastbusiness$balance$item", "FastBusiness¤Balance¤Item @${1:DateFrom}, @Unit, @Site, @Item, ${2|1,2|}, 2, @DataType, @UserID, @Admin, @Join, @Key", "tồn kho theo kho / vật tư"),
        new("fastbusiness$balance$job", "FastBusiness¤Balance¤Job @${1:DateFrom}, @Unit, @Account, @Job, ${2|1,2|}, 2, @UserID, @Admin, @Join, @Key", "số dư theo vụ việc"),
        new("fastbusiness$function$getbalancedateandperiod", "dbo.FastBusiness¤Function¤GetBalanceDateAndPeriod(@${1:Date}, ${2|1,2|})", "5/13 lần với (@Date, @BalanceType) — ngày + kỳ của số dư"),
        new("fastbusiness$function$cf$getcfgroupfilter", "dbo.FastBusiness¤Function¤CF¤GetCFGroupFilter('${1:a.nhom_hn}', @${2:cfGroup}, @userID, @admin)", "13/13 lần gọi"),
    };

    /// <summary>Ghi chú theo nhóm hàm FBO — hiện ở trang hướng dẫn (mục "Ghi chú theo nhóm"): nhóm này gồm những hàm nào, gõ gì để gọi, ý nghĩa tham số (đánh dấu rõ chỗ nào là suy ra).</summary>
    public static readonly (string Title, string Prefixes, string Text)[] GroupNotes =
    {
        ("Số dư (họ FastBusiness$Balance$*)", "bal… (balacc, balcust, baljob, balitem, balctc…) · exec FastBusiness$Balance$ · GetBalanceDateAndPeriod",
            "Họ ~30 procedure tính số dư (Account, AccountUnit, Customer, Item, Job, Lot, Contract…). Cách dùng trong code FBO: tạo sẵn bảng tạm đúng cột kết quả rồi INSERT #bảng EXEC FastBusiness$Balance$… . " +
            "@BalanceType: 1 = số dư đầu (truyền @DateFrom), 2 = số dư cuối (truyền @DateTo) — SUY RA từ cách các procedure khác gọi, chưa có tài liệu xác nhận. @ResultType chỉ thấy giá trị 1 và 2 (đa số 2), chưa rõ ý nghĩa. " +
            "Gõ balacc / balaccu / balcust / balcustu / baljob / balitem / balctc / balfee / baljobc / balcat → ra đủ: SELECT TOP 0 … INTO #bảng FROM cd… (struct), dựng @Key bằng GetCheckKey, rồi INSERT … EXEC (balctc, balfee, baljobc, balcat là các hàm *_new theo đúng mẫu của dự án). Hoặc gõ exec FastBusiness$Balance$ rồi chọn hàm."),
        ("Điều kiện lọc @Key (khách hàng, vật tư, kho, bộ phận…)", "fcust · fitem · fsite · fdept · fjob · fcon · freason · fitype · fprod · fsales · fcur · fmo · flot · fitemgrp · fcgrp · ftrans · fvc · flike · feq · fin · finlist · frange · fkey · fdel",
            "Mỗi mẫu là MỘT điều kiện nối vào @Key, theo đúng dạng hay gặp nhất (số procedure dùng ghi ở mô tả). LIKE tiền tố (flike, fcust, fitem, fsite…): a.cột like 'giá trị%'. Bằng chính xác: feq. Danh sách chính xác (IN ('A','B')): fin, ftrans, fvc. Danh sách TIỀN TỐ cách nhau dấu phẩy (như LIKE 'x%' cho từng mục): finlist — khác fin ở chỗ khớp đầu chuỗi. Từ … đến … theo số chứng từ (PadL): frange. Gắn điều kiện đã dựng sẵn (@UnitKey, @SiteKey, @AccountKey): fkey. Bỏ chứng từ đã xoá: fdel. Biến/ cột mặc định (vd @Customer → a.ma_kh) lấy từ cặp hay gặp nhất, đổi bằng Tab."),
        ("Khung procedure", "rsrep · rsrepq · rspivot · rsfn",
            "rsrep = khung gọn; rsrepq = khung đầy đủ (@UnitKey, @Key + GetCheckKey, Partition$Execute vào #report, SELECT); rspivot = khung báo cáo PIVOT theo mẫu rs_rptExpensesAggregationReportPivot (dữ liệu thô #tmp, #report sysorder 5/1/9, SELECT kết quả, bảng #pivot mô tả cột động) — điền truy vấn chi tiết ở dòng chú thích; rsfn = function vô hướng."),
        ("Lọc dữ liệu (đơn vị, tài khoản, kho)", "unit · key · rsfilter · acckey · sitekey · part",
            "GetUnitFilter → @UnitKey, GetAccountKey / GetSiteFilter → điều kiện tài khoản / kho (OUTPUT), GetCheckKey chuẩn hoá @Key, Partition$Execute chạy truy vấn theo kỳ. Đa số báo cáo rs_ ghép các khối này lại."),
        ("Báo cáo chuẩn", "rsrep · sysrep · bil",
            "rsrep ra cả khung procedure báo cáo; sysrep ra ba cột sysorder/sysprint/systotal; bil ra tên song ngữ theo @Language."),
    };

    /// <summary>Các tham số chuẩn của báo cáo và kiểu khai báo — dùng cho cảnh báo "dùng mà chưa khai báo" + sửa nhanh thêm tham số.</summary>
    public static readonly (string Name, string Type)[] StandardParams =
    {
        ("@Language", "CHAR(1)"), ("@UserID", "INT"), ("@Admin", "BIT"), ("@Unit", "VARCHAR(1023)"),
        ("@DateFrom", "SMALLDATETIME"), ("@DateTo", "SMALLDATETIME"),
    };

    /// <summary>Gói tĩnh (mẫu + cách gọi + thứ tự ưu tiên + tham số chuẩn) gửi sang trang editor một lần khi editor sẵn sàng.</summary>
    public static object ToEditorPayload() => new
    {
        snippets = Snippets.Select(s => new { p = s.Prefix, n = s.Name, d = s.Description, c = s.Category, b = s.Body }),
        idioms = Idioms.Select(i => new { r = i.RoutineName, t = i.Insert, n = i.Note }),
        popular = SqlHintPopular.Names,
        stdParams = StandardParams.Select(p => new { n = p.Name, t = p.Type }),
    };

    /// <summary>Nội dung trang trợ giúp (Settings → "Hướng dẫn gợi ý code SQL"): mỗi mục = bạn gõ gì → xuất hiện gì.</summary>
    public static object ToHelpModel() => new
    {
        snippets = Snippets.Select(s => new { prefix = s.Prefix, name = s.Name, desc = s.Description, category = s.Category, body = s.Body.Replace('¤', '$') }),
        idioms = Idioms.Select(i => new { routine = i.RoutineName, insert = i.Insert.Replace('¤', '$'), note = i.Note }),
        popularCount = SqlHintPopular.Names.Length,
        notes = GroupNotes.Select(n => new { title = n.Title, prefixes = n.Prefixes, text = n.Text }),
        smart = SmartSnippets.Select(x => new { usage = x.Usage, example = x.Example, text = x.Text.Replace('¤', '$') }),
    };
}
