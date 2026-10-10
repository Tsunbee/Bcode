ALTER PROCEDURE [dbo].[rs_rptInDirectCashflow] -- fs20_CashflowID
	@dateFrom AS SMALLDATETIME,
	@dateTo AS SMALLDATETIME,
	@datePreviousBegin AS SMALLDATETIME,
	@datePreviousEnd AS SMALLDATETIME,
	@unit VARCHAR(1023),
	@form CHAR(16),
	@reportType CHAR(1), -- 1 - Mau nam, 2 - Mau giua nien do
	@language CHAR(1),
	@userID INT,
	@admin BIT,
	@resultTable VARCHAR(32) = ''
AS
BEGIN
	SET NOCOUNT ON
	SET ANSI_NULLS OFF
	DECLARE @kind CHAR(1), @ma_so VARCHAR(33), @cong_no NUMERIC(1, 0), @no_co NUMERIC(1, 0), @dau_cuoi NUMERIC(1, 0), @khong_am NUMERIC(1, 0), @tk VARCHAR(1023), @tk_du VARCHAR(1023)
		, @cach_tinh NVARCHAR(1024), @strSql NVARCHAR(4000)
		, @ky_nay NUMERIC(28, 6), @ky_truoc NUMERIC(28, 6), @ky_nay_nt NUMERIC(28, 6), @ky_truoc_nt NUMERIC(28, 6)
		, @lk_kn NUMERIC(28, 6), @lk_kt NUMERIC(28, 6), @lk_kn_nt NUMERIC(28, 6), @lk_kt_nt NUMERIC(28, 6)

	DECLARE @queryType TINYINT
	IF EXISTS(SELECT 1 FROM options WHERE name = 'c_013' AND val = '1') SELECT @queryType = 1 ELSE SELECT @queryType = 0

	DECLARE @dateFrom0 SMALLDATETIME, @dateTo0 SMALLDATETIME, @datePreviousBegin0 SMALLDATETIME, @datePreviousEnd0 SMALLDATETIME, @d1 SMALLDATETIME, @d2 SMALLDATETIME, @xType CHAR(1)
	DECLARE @unitKey NVARCHAR(4000), @s NVARCHAR(4000), @s2 NVARCHAR(4000), @s3 NVARCHAR(4000), @q NVARCHAR(4000)
	DECLARE @p1 INT, @y1 INT, @p2 INT, @y2 INT, @dStart1 SMALLDATETIME, @dEnd1 SMALLDATETIME, @dStart2 SMALLDATETIME, @dEnd2 SMALLDATETIME, @type VARCHAR(4)

	SELECT @dateFrom0 = dbo.ff_BgDate(@dateFrom), @dateTo0 = @dateFrom - 1, @datePreviousBegin0 = dbo.ff_BgDate(@datePreviousBegin), @datePreviousEnd0 = @datePreviousBegin - 1
	SELECT @d1 = CONVERT(VARCHAR(6), @dateTo, 112) + '01', @d2 = CONVERT(VARCHAR(6), @datePreviousEnd, 112) + '01'
	SELECT @d1 = CASE WHEN @d1 < @dateFrom THEN @dateFrom ELSE @d1 END, @d2 = CASE WHEN @d2 < @datePreviousBegin THEN @datePreviousBegin ELSE @d2 END

	-- Struct
	SELECT * INTO #report FROM v20gltc6 WHERE form = @form
	SELECT TOP 0 tk, tk_du, ma_kh, ps_no, ps_co, ps_no_nt, ps_co_nt INTO #ct00Tmp FROM wrkgl
	SELECT TOP 0 tk, tk_du, ps_no, ps_co, ps_no_nt, ps_co_nt, CAST('' AS VARCHAR(4)) AS type INTO #ct00 FROM wrkgl
	SELECT TOP 0 tk, ma_kh, ps_no, ps_co, ps_no_nt, ps_co_nt, CAST('' AS VARCHAR(4)) AS type INTO #ct004Cust FROM wrkgl
	--
	SELECT TOP 0 tk, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt INTO #tempAccts FROM cdtk
	SELECT TOP 0 tk, ma_kh, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt INTO #tempCusts FROM cdkh
	--
	SELECT TOP 0 *, CAST('' AS VARCHAR(33)) AS type INTO #BgAccts FROM #tempAccts
	SELECT TOP 0 *, CAST('' AS VARCHAR(33)) AS type INTO #EdAccts FROM #tempAccts
	SELECT TOP 0 *, CAST('' AS VARCHAR(33)) AS type INTO #BgCusts FROM #tempCusts
	SELECT TOP 0 *, CAST('' AS VARCHAR(33)) AS type INTO #EdCusts FROM #tempCusts

	-- Xử lý lấy lại tài khoản chi tiết theo khai báo mẫu
	DECLARE @accountKey NVARCHAR(4000), @$tk NVARCHAR(4000), @$tk_du NVARCHAR(4000), @i INT

	SELECT TOP 0 a.ma_so, a.tk INTO #$tk FROM v20gltc6 a RIGHT JOIN (SELECT TOP 0 NULL a) b ON 1 = 0
	SELECT TOP 0 a.ma_so, a.tk_du INTO #$tk_du FROM v20gltc6 a RIGHT JOIN (SELECT TOP 0 NULL a) b ON 1 = 0

	EXEC dbo.FastBusiness$App$ReportForm$GetAccountKey 'tkÿtk_du', 'like', '#$tk:tkÿ#$tk_du:tk_du', '#report:ma_so:tkÿ#report:ma_so:tk_du', @userID, @admin, @accountKey OUTPUT, 2
	SELECT @$tk = dbo.FastBusiness$Function$GetWordNum(@accountKey, 1, CHAR(255)), @$tk_du = dbo.FastBusiness$Function$GetWordNum(@accountKey, 2, CHAR(255))

	SELECT TOP 0 ma_so, tk AS tk0, tk INTO #$tk0 FROM #$tk
	EXEC dbo.FastBusiness$App$ReportForm$GetAccountKey 'tk', 'like', '#$tk0:tk', '#report:ma_so:tk', @userID, @admin, @accountKey OUTPUT, 3
	--
	-- Check Formula
	EXEC FastBusiness$Report$CheckFormula '#report', @userID, @admin

	-- Unit Key
	SET @unitKey = dbo.FastBusiness$Function$System$GetUnitFilter('ma_dvcs', @unit, @userID, @admin)
	--
	SET @unitKey = dbo.FastBusiness$Function$System$GetCheckKey(@unitKey)

	SET @s = 'insert into #ct00Tmp'
	SET @s = @s + ' select tk, tk_du, ma_kh, sum(ps_no), sum(ps_co), sum(ps_no_nt), sum(ps_co_nt)'
	SET @s = @s + ' from r00$%Partition a with(nolock) where %[status = ''1''' + CASE WHEN @unitKey IS NOT NULL THEN ' and ' + @unitKey ELSE '' END + ']%'
	SET @s = @s + ' group by tk, tk_du, ma_kh'
	--
	SET @s2 = 'insert into #ct00'
	SET @s2 = @s2 + ' select tk, tk_du, sum(ps_no), sum(ps_co), sum(ps_no_nt), sum(ps_co_nt), ''@type'''
	SET @s2 = @s2 + ' from pstkdu$%Partition a with(nolock) where %[' + CASE WHEN @unitKey IS NOT NULL THEN @unitKey ELSE '' END + ']%'
	SET @s2 = @s2 + ' group by tk, tk_du'
	--
	SET @s3 = 'insert into #ct004Cust'
	SET @s3 = @s3 + ' select tk, ma_kh, sum(ps_no), sum(ps_co), sum(ps_no_nt), sum(ps_co_nt), ''@type'''
	SET @s3 = @s3 + ' from pskh$%Partition a with(nolock) where %[' + CASE WHEN @unitKey IS NOT NULL THEN @unitKey ELSE '' END + ']%'
	SET @s3 = @s3 + ' group by tk, ma_kh'

	-- Nam nay
	SELECT @type = 'KN'
	SELECT @p1 = p1, @y1 = y1, @p2 = p2, @y2 = y2, @dStart1 = dStart1, @dEnd1 = dEnd1, @dStart2 = dStart2, @dEnd2 = dEnd2 FROM dbo.FastBusiness$Function$GetArisingDateAndPeriod(@dateFrom, @dateTo)
	DELETE #ct00Tmp
	EXEC FastBusiness$Partition$Execute @s, NULL, 'ngay_ct', @dStart1, @dEnd1, @userID, @admin
	EXEC FastBusiness$Partition$Execute @s, NULL, 'ngay_ct', @dStart2, @dEnd2, @userID, @admin
	INSERT INTO #ct00 SELECT tk, tk_du, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), @type FROM #ct00Tmp GROUP BY tk, tk_du
	INSERT INTO #ct004Cust SELECT tk, ma_kh, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), @type FROM #ct00Tmp GROUP BY tk, ma_kh
	IF @y1 <> 0 BEGIN
		SELECT @dStart1 = dbo.ff_GetStartDateOfCycle(@p1, @y1), @dEnd1 = dbo.ff_GetEndDateOfCycle(@p2, @y2)
		--
		SET @q = REPLACE(@s2, '@type', @type)
		EXEC FastBusiness$Partition$Execute @q, NULL, NULL, @dStart1, @dEnd1, @userID, @admin
		--
		SET @q = REPLACE(@s3, '@type', @type)
		EXEC FastBusiness$Partition$Execute @q, NULL, NULL, @dStart1, @dEnd1, @userID, @admin
	END

	-- Nam truoc
	SELECT @type = 'KT'
	SELECT @p1 = p1, @y1 = y1, @p2 = p2, @y2 = y2, @dStart1 = dStart1, @dEnd1 = dEnd1, @dStart2 = dStart2, @dEnd2 = dEnd2 FROM dbo.FastBusiness$Function$GetArisingDateAndPeriod(@datePreviousBegin, @datePreviousEnd)
	DELETE #ct00Tmp
	EXEC FastBusiness$Partition$Execute @s, NULL, 'ngay_ct', @dStart1, @dEnd1, @userID, @admin
	EXEC FastBusiness$Partition$Execute @s, NULL, 'ngay_ct', @dStart2, @dEnd2, @userID, @admin
	INSERT INTO #ct00 SELECT tk, tk_du, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), @type FROM #ct00Tmp GROUP BY tk, tk_du
	INSERT INTO #ct004Cust SELECT tk, ma_kh, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), @type FROM #ct00Tmp GROUP BY tk, ma_kh
	IF @y1 <> 0 BEGIN
		SELECT @dStart1 = dbo.ff_GetStartDateOfCycle(@p1, @y1), @dEnd1 = dbo.ff_GetEndDateOfCycle(@p2, @y2)
		--
		SET @q = REPLACE(@s2, '@type', @type)
		EXEC FastBusiness$Partition$Execute @q, NULL, NULL, @dStart1, @dEnd1, @userID, @admin
		--
		SET @q = REPLACE(@s3, '@type', @type)
		EXEC FastBusiness$Partition$Execute @q, NULL, NULL, @dStart1, @dEnd1, @userID, @admin
	END

	-- Luy ke nam nay
	SELECT @type = 'LKKN'
	SELECT @p1 = p1, @y1 = y1, @p2 = p2, @y2 = y2, @dStart1 = dStart1, @dEnd1 = dEnd1, @dStart2 = dStart2, @dEnd2 = dEnd2 FROM dbo.FastBusiness$Function$GetArisingDateAndPeriod(@dateFrom0, @dateTo0)
	DELETE #ct00Tmp
	EXEC FastBusiness$Partition$Execute @s, NULL, 'ngay_ct', @dStart1, @dEnd1, @userID, @admin
	EXEC FastBusiness$Partition$Execute @s, NULL, 'ngay_ct', @dStart2, @dEnd2, @userID, @admin
	INSERT INTO #ct00 SELECT tk, tk_du, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), @type FROM #ct00Tmp GROUP BY tk, tk_du
	INSERT INTO #ct004Cust SELECT tk, ma_kh, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), @type FROM #ct00Tmp GROUP BY tk, ma_kh
	IF @y1 <> 0 BEGIN
		SELECT @dStart1 = dbo.ff_GetStartDateOfCycle(@p1, @y1), @dEnd1 = dbo.ff_GetEndDateOfCycle(@p2, @y2)
		--
		SET @q = REPLACE(@s2, '@type', @type)
		EXEC FastBusiness$Partition$Execute @q, NULL, NULL, @dStart1, @dEnd1, @userID, @admin
		--
		SET @q = REPLACE(@s3, '@type', @type)
		EXEC FastBusiness$Partition$Execute @q, NULL, NULL, @dStart1, @dEnd1, @userID, @admin
	END

	-- Luy ke nam truoc
	SELECT @type = 'LKKT'
	SELECT @p1 = p1, @y1 = y1, @p2 = p2, @y2 = y2, @dStart1 = dStart1, @dEnd1 = dEnd1, @dStart2 = dStart2, @dEnd2 = dEnd2 FROM dbo.FastBusiness$Function$GetArisingDateAndPeriod(@datePreviousBegin0, @datePreviousEnd0)
	DELETE #ct00Tmp
	EXEC FastBusiness$Partition$Execute @s, NULL, 'ngay_ct', @dStart1, @dEnd1, @userID, @admin
	EXEC FastBusiness$Partition$Execute @s, NULL, 'ngay_ct', @dStart2, @dEnd2, @userID, @admin
	INSERT INTO #ct00 SELECT tk, tk_du, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), @type FROM #ct00Tmp GROUP BY tk, tk_du
	INSERT INTO #ct004Cust SELECT tk, ma_kh, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), @type FROM #ct00Tmp GROUP BY tk, ma_kh
	IF @y1 <> 0 BEGIN
		SELECT @dStart1 = dbo.ff_GetStartDateOfCycle(@p1, @y1), @dEnd1 = dbo.ff_GetEndDateOfCycle(@p2, @y2)
		--
		SET @q = REPLACE(@s2, '@type', @type)
		EXEC FastBusiness$Partition$Execute @q, NULL, NULL, @dStart1, @dEnd1, @userID, @admin
		--
		SET @q = REPLACE(@s3, '@type', @type)
		EXEC FastBusiness$Partition$Execute @q, NULL, NULL, @dStart1, @dEnd1, @userID, @admin
	END

	-- Balance
	DECLARE @WhereClause NVARCHAR(4000)
	SET @WhereClause = CASE WHEN @unitKey IS NOT NULL THEN @unitKey ELSE '' END

	DELETE #tempAccts
	INSERT INTO #tempAccts EXEC FastBusiness$Balance$Account @dateFrom0, NULL, '', 1, 2, @userID, @admin, '', @WhereClause
	INSERT INTO #BgAccts SELECT *, 'DateFrom0' FROM #tempAccts
	INSERT INTO #BgAccts SELECT *, 'DateFrom' FROM #tempAccts
	INSERT INTO #BgAccts SELECT tk, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), 'DateFrom' FROM #ct00 WHERE type = 'LKKN' GROUP BY tk
	INSERT INTO #EdAccts SELECT *, 'DateTo' FROM #tempAccts
	INSERT INTO #EdAccts SELECT tk, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), 'DateTo' FROM #ct00 WHERE type = 'LKKN' OR type = 'KN' GROUP BY tk

	DELETE #tempAccts
	INSERT INTO #tempAccts EXEC FastBusiness$Balance$Account @datePreviousBegin0, NULL, '', 1, 2, @userID, @admin, '', @WhereClause
	INSERT INTO #BgAccts SELECT *, 'DatePreviousBegin0' FROM #tempAccts
	INSERT INTO #BgAccts SELECT *, 'DatePreviousBegin' FROM #tempAccts
	INSERT INTO #BgAccts SELECT tk, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), 'DatePreviousBegin' FROM #ct00 WHERE type = 'LKKT' GROUP BY tk
	INSERT INTO #EdAccts SELECT *, 'DatePreviousEnd' FROM #tempAccts
	INSERT INTO #EdAccts SELECT tk, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), 'DatePreviousEnd' FROM #ct00 WHERE type = 'LKKT' OR type = 'KT' GROUP BY tk

	SET @WhereClause = @WhereClause + CASE WHEN @WhereClause <> '' THEN ' and ' ELSE '' END + 'b.tk_cn = 1'

	DELETE #tempCusts
	INSERT INTO #tempCusts EXEC FastBusiness$Balance$Customer @dateFrom0, NULL, '', '', 1, 2, @userID, @admin, 'join dmtk b on a.tk = b.tk', @WhereClause
	INSERT INTO #BgCusts SELECT *, 'DateFrom0' FROM #tempCusts
	INSERT INTO #BgCusts SELECT *, 'DateFrom' FROM #tempCusts
	INSERT INTO #BgCusts SELECT tk, ma_kh, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), 'DateFrom' FROM #ct004Cust WHERE type = 'LKKN' AND ma_kh IS NOT NULL GROUP BY tk, ma_kh
	INSERT INTO #EdCusts SELECT *, 'DateTo' FROM #tempCusts
	INSERT INTO #EdCusts SELECT tk, ma_kh, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), 'DateTo' FROM #ct004Cust WHERE (type = 'LKKN' OR type = 'KN') AND ma_kh IS NOT NULL GROUP BY tk, ma_kh

	DELETE #tempCusts
	INSERT INTO #tempCusts EXEC FastBusiness$Balance$Customer @datePreviousBegin0, NULL, '', '', 1, 2, @userID, @admin, 'join dmtk b on a.tk = b.tk', @WhereClause
	INSERT INTO #BgCusts SELECT *, 'DatePreviousBegin0' FROM #tempCusts
	INSERT INTO #BgCusts SELECT *, 'DatePreviousBegin' FROM #tempCusts
	INSERT INTO #BgCusts SELECT tk, ma_kh, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), 'DatePreviousBegin' FROM #ct004Cust WHERE type = 'LKKT' AND ma_kh IS NOT NULL GROUP BY tk, ma_kh
	INSERT INTO #EdCusts SELECT *, 'DatePreviousEnd' FROM #tempCusts
	INSERT INTO #EdCusts SELECT tk, ma_kh, SUM(ps_no), SUM(ps_co), SUM(ps_no_nt), SUM(ps_co_nt), 'DatePreviousEnd' FROM #ct004Cust WHERE (type = 'LKKT' OR type = 'KT') AND ma_kh IS NOT NULL  GROUP BY tk, ma_kh
	--

	DECLARE Cal_Cur CURSOR FOR
	SELECT Kind, Ma_so, Cong_no, No_co, Dau_cuoi, Khong_am, tk, Tk_du, [type] FROM #report WHERE Ma_so <> '' AND kind <> '0'
	OPEN Cal_Cur
	FETCH NEXT FROM Cal_Cur INTO @kind, @ma_so, @cong_no, @no_co, @dau_cuoi, @khong_am, @tk, @tk_du, @xType
	WHILE @@FETCH_STATUS = 0 BEGIN
		IF @kind = '1' BEGIN
			IF @xType = '1' BEGIN
				SELECT @q = '
select @ky_nay = sum(' + CASE WHEN @no_co = 1 THEN 'ps_no' ELSE 'ps_co' END + '), @ky_nay_nt = sum(' + CASE WHEN @no_co = 1 THEN 'ps_no_nt' ELSE 'ps_co_nt' END + ') from r00$' + CONVERT(VARCHAR(6), @dateTo, 112) + ' a with(nolock)
	where (ngay_ct between @d1 and @dateTo) and status = ''1''' + CASE WHEN @unitKey IS NOT NULL THEN ' and ' + @unitKey ELSE '' END + ' and (' + CASE WHEN @no_co = 1 THEN 'ps_no + ps_no_nt' ELSE 'ps_co + ps_co_nt' END + ' <> 0)' +
CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END +
CASE WHEN @tk_du <> '' THEN ' and ' + @$tk_du ELSE '' END
				IF OBJECT_ID (N'dbo.r00$' + CONVERT(VARCHAR(6), @dateTo, 112), N'U') IS NOT NULL
					EXEC sp_executesql @q,
						N'@ma_so varchar(33), @d1 smalldatetime, @dateTo smalldatetime, @tk char(512), @tk_du char(512), @ky_nay numeric(28, 6) output, @ky_nay_nt numeric(28, 6) output',
						@ma_so, @d1, @dateTo, @tk, @tk_du, @ky_nay OUTPUT, @ky_nay_nt OUTPUT
				SELECT @q = '
select @ky_truoc = sum(' + CASE WHEN @no_co = 1 THEN 'ps_no' ELSE 'ps_co' END + '), @ky_truoc_nt = sum(' + CASE WHEN @no_co = 1 THEN 'ps_no_nt' ELSE 'ps_co_nt' END + ') from r00$' + CONVERT(VARCHAR(6), @datePreviousEnd, 112) + ' a with(nolock)
	where (ngay_ct between @d2 and @datePreviousEnd) and status = ''1''' + CASE WHEN @unitKey IS NOT NULL THEN ' and ' + @unitKey ELSE '' END + ' and (' + CASE WHEN @no_co = 1 THEN 'ps_no + ps_no_nt' ELSE 'ps_co + ps_co_nt' END + ' <> 0)' +
CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END +
CASE WHEN @tk_du <> '' THEN ' and ' + @$tk_du ELSE '' END
				IF OBJECT_ID (N'dbo.r00$' + CONVERT(VARCHAR(6), @datePreviousEnd, 112), N'U') IS NOT NULL
					EXEC sp_executesql @q,
						N'@ma_so varchar(33), @d2 smalldatetime, @datePreviousEnd smalldatetime, @tk char(512), @tk_du char(512), @ky_truoc numeric(28, 6) output, @ky_truoc_nt numeric(28, 6) output',
						@ma_so, @d2, @datePreviousEnd, @tk, @tk_du, @ky_truoc OUTPUT, @ky_truoc_nt OUTPUT
				UPDATE #report SET Ky_nay = @ky_nay, Ky_truoc = @ky_truoc, Ky_naynt = @ky_nay_nt, Ky_truocnt = @ky_truoc_nt, Lk_kn = @ky_nay, Lk_kt = @ky_truoc, Lk_kn_nt = @ky_nay_nt, Lk_kt_nt = @ky_truoc_nt WHERE Ma_so = @ma_so
				GOTO Next
			END
			SELECT @q = '
select @ky_nay = sum(case when type = ''KN'' then case when @no_co = 1 then ps_no else ps_co end else 0 end)
	, @ky_nay_nt = sum(case when type = ''KN'' then case when @no_co = 1 then ps_no_nt else ps_co_nt end else 0 end)
	, @lk_kn = sum(case when type = ''LKKN'' or type = ''KN'' then case when @no_co = 1 then ps_no else ps_co end else 0 end)
	, @lk_kn_nt = sum(case when type = ''LKKN'' or type = ''KN'' then case when @no_co = 1 then ps_no_nt else ps_co_nt end else 0 end)
	from #ct00 a
	where (type = ''KN'' or type = ''LKKN'')
		and case when @no_co = 1 then ps_no + ps_no_nt else ps_co + ps_co_nt end <> 0' +
CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END +
CASE WHEN @tk_du <> '' THEN ' and ' + @$tk_du ELSE '' END + '
select @ky_truoc = sum(case when type = ''KT'' then case when @no_co = 1 then ps_no else ps_co end else 0 end)
	, @ky_truoc_nt = sum(case when type = ''KT'' then case when @no_co = 1 then ps_no_nt else ps_co_nt end else 0 end)
	, @lk_kt = sum(case when type = ''LKKT'' or type = ''KT'' then case when @no_co = 1 then ps_no else ps_co end else 0 end)
	, @lk_kt_nt = sum(case when type = ''LKKT'' or type = ''KT'' then case when @no_co = 1 then ps_no_nt else ps_co_nt end else 0 end)
	from #ct00 a
	where (type = ''KT'' or type = ''LKKT'')
		and case when @no_co = 1 then ps_no + ps_no_nt else ps_co + ps_co_nt end <> 0' +
CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END +
CASE WHEN @tk_du <> '' THEN ' and ' + @$tk_du ELSE '' END
			EXEC sp_executesql @q,
				N'@ma_so varchar(33), @no_co numeric(1, 0), @tk char(512), @tk_du char(512), @ky_nay numeric(28, 6) output, @ky_nay_nt numeric(28, 6) output, @lk_kn numeric(28, 6) output, @lk_kn_nt numeric(28, 6) output, @ky_truoc numeric(28, 6) output, @ky_truoc_nt numeric(28, 6) output, @lk_kt numeric(28, 6) output, @lk_kt_nt numeric(28, 6) output',
				@ma_so, @no_co, @tk, @tk_du, @ky_nay OUTPUT, @ky_nay_nt OUTPUT, @lk_kn OUTPUT, @lk_kn_nt OUTPUT, @ky_truoc OUTPUT, @ky_truoc_nt OUTPUT, @lk_kt OUTPUT, @lk_kt_nt OUTPUT
		END

		IF @kind = '2' AND @cong_no = 0 AND @dau_cuoi = 1 BEGIN
			SELECT @q = '
select @ky_nay = sum(
	case
		when @no_co = 1 and @khong_am = 0 then du_no - du_co
		when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no - du_co, 0)
		when @no_co = 2 and @khong_am = 0 then du_co - du_no
		when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co - du_no, 0)
		else 0
	end
	)
, @ky_nay_nt = sum(
	case
		when @no_co = 1 and @khong_am = 0 then du_no_nt - du_co_nt
		when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no_nt - du_co_nt, 0)
		when @no_co = 2 and @khong_am = 0 then du_co_nt - du_no_nt
		when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co_nt - du_no_nt, 0)
		else 0
	end
	)
from (
	select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
		, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
		, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
		, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
		from #BgAccts a $join$
		where type = ''DateFrom''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
		group by $group$
	) a

select @ky_truoc = sum(
	case
		when @no_co = 1 and @khong_am = 0 then du_no - du_co
		when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no - du_co, 0)
		when @no_co = 2 and @khong_am = 0 then du_co - du_no
		when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co - du_no, 0)
		else 0
	end
	)
, @ky_truoc_nt = sum(
	case
		when @no_co = 1 and @khong_am = 0 then du_no_nt - du_co_nt
		when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no_nt - du_co_nt, 0)
		when @no_co = 2 and @khong_am = 0 then du_co_nt - du_no_nt
		when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co_nt - du_no_nt, 0)
		else 0
	end
	)
from (
	select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
		, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
		, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
		, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
		from #BgAccts a $join$
		where type = ''DatePreviousBegin''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
		group by $group$
	) a
'
			IF @queryType = 1 SELECT @q = REPLACE(REPLACE(@q, '$join$', ''), '$group$', 'dbo.ff_GetWordNum(@tk, dbo.ff_Inlist2(tk, @tk))')
			ELSE SELECT @q = REPLACE(REPLACE(@q, '$join$', ' left join #$tk0 b on a.tk = b.tk and b.ma_so = @ma_so'), '$group$', 'b.tk0')

			EXEC sp_executesql @q,
				N'@ma_so varchar(33), @no_co numeric(1, 0), @khong_am numeric(1, 0), @tk char(512), @ky_nay numeric(28, 6) output, @ky_nay_nt numeric(28, 6) output, @ky_truoc numeric(28, 6) output, @ky_truoc_nt numeric(28, 6) output',
				@ma_so, @no_co, @khong_am, @tk, @ky_nay OUTPUT, @ky_nay_nt OUTPUT, @ky_truoc OUTPUT, @ky_truoc_nt OUTPUT

			IF @reportType = '2' BEGIN
				SELECT @q = '
select @lk_kn = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no - du_co
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no - du_co, 0)
			when @no_co = 2 and @khong_am = 0 then du_co - du_no
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co - du_no, 0)
			else 0
		end
		)
	, @lk_kn_nt = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no_nt - du_co_nt
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no_nt - du_co_nt, 0)
			when @no_co = 2 and @khong_am = 0 then du_co_nt - du_no_nt
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co_nt - du_no_nt, 0)
			else 0
		end
		)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #BgAccts a $join$
			where type = ''DateFrom0''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$
		) a

select @lk_kt = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no - du_co
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no - du_co, 0)
			when @no_co = 2 and @khong_am = 0 then du_co - du_no
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co - du_no, 0)
			else 0
		end
		)
	, @lk_kt_nt = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no_nt - du_co_nt
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no_nt - du_co_nt, 0)
			when @no_co = 2 and @khong_am = 0 then du_co_nt - du_no_nt
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co_nt - du_no_nt, 0)
			else 0
		end
		)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #BgAccts a $join$
			where type = ''DatePreviousBegin0''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$
		) a'
				IF @queryType = 1 SELECT @q = REPLACE(REPLACE(@q, '$join$', ''), '$group$', 'dbo.ff_GetWordNum(@tk, dbo.ff_Inlist2(tk, @tk))')
				ELSE SELECT @q = REPLACE(REPLACE(@q, '$join$', ' left join #$tk0 b on a.tk = b.tk and b.ma_so = @ma_so'), '$group$', 'b.tk0')

				EXEC sp_executesql @q,
					N'@ma_so varchar(33), @no_co numeric(1, 0), @khong_am numeric(1, 0), @tk char(512), @lk_kn numeric(28, 6) output, @lk_kn_nt numeric(28, 6) output, @lk_kt numeric(28, 6) output, @lk_kt_nt numeric(28, 6) output',
					@ma_so, @no_co, @khong_am, @tk, @lk_kn OUTPUT, @lk_kn_nt OUTPUT, @lk_kt OUTPUT, @lk_kt_nt OUTPUT

			END
		END

		IF @kind = '2' AND @cong_no = 0 AND @dau_cuoi = 2 BEGIN
			SELECT @q = '
select @ky_nay = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no - du_co
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no - du_co, 0)
			when @no_co = 2 and @khong_am = 0 then du_co - du_no
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co - du_no, 0)
			else 0
		end
		)
	, @ky_nay_nt = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no_nt - du_co_nt
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no_nt - du_co_nt, 0)
			when @no_co = 2 and @khong_am = 0 then du_co_nt - du_no_nt
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co_nt - du_no_nt, 0)
			else 0
		end
		)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #EdAccts a $join$
			where type = ''DateTo''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$
		) a

select @ky_truoc = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no - du_co
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no - du_co, 0)
			when @no_co = 2 and @khong_am = 0 then du_co - du_no
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co - du_no, 0)
			else 0
		end
		)
	, @ky_truoc_nt = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no_nt - du_co_nt
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no_nt - du_co_nt, 0)
			when @no_co = 2 and @khong_am = 0 then du_co_nt - du_no_nt
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co_nt - du_no_nt, 0)
			else 0
		end
		)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #EdAccts a $join$
			where type = ''DatePreviousEnd''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$
		) a
'
			IF @queryType = 1 SELECT @q = REPLACE(REPLACE(@q, '$join$', ''), '$group$', 'dbo.ff_GetWordNum(@tk, dbo.ff_Inlist2(tk, @tk))')
			ELSE SELECT @q = REPLACE(REPLACE(@q, '$join$', ' left join #$tk0 b on a.tk = b.tk and b.ma_so = @ma_so'), '$group$', 'b.tk0')

			EXEC sp_executesql @q,
				N'@ma_so varchar(33), @no_co numeric(1, 0), @khong_am numeric(1, 0), @tk char(512), @ky_nay numeric(28, 6) output, @ky_nay_nt numeric(28, 6) output, @ky_truoc numeric(28, 6) output, @ky_truoc_nt numeric(28, 6) output',
				@ma_so, @no_co, @khong_am, @tk, @ky_nay OUTPUT, @ky_nay_nt OUTPUT, @ky_truoc OUTPUT, @ky_truoc_nt OUTPUT

			IF @reportType = '2' BEGIN
				SELECT @q = '
select @lk_kn = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no - du_co
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no - du_co, 0)
			when @no_co = 2 and @khong_am = 0 then du_co - du_no
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co - du_no, 0)
			else 0
		end
		)
	, @lk_kn_nt = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no_nt - du_co_nt
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no_nt - du_co_nt, 0)
			when @no_co = 2 and @khong_am = 0 then du_co_nt - du_no_nt
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co_nt - du_no_nt, 0)
			else 0
		end
		)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #EdAccts a $join$
			where type = ''DateTo''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$
		) a

select @lk_kt = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no - du_co
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no - du_co, 0)
			when @no_co = 2 and @khong_am = 0 then du_co - du_no
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co - du_no, 0)
			else 0
		end
		)
	, @lk_kt_nt = sum(
		case
			when @no_co = 1 and @khong_am = 0 then du_no_nt - du_co_nt
			when @no_co = 1 and @khong_am = 1 then dbo.ff_Max(du_no_nt - du_co_nt, 0)
			when @no_co = 2 and @khong_am = 0 then du_co_nt - du_no_nt
			when @no_co = 2 and @khong_am = 1 then dbo.ff_Max(du_co_nt - du_no_nt, 0)
			else 0
		end
		)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #EdAccts a $join$
			where type = ''DatePreviousEnd''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$
		) a
'
				IF @queryType = 1 SELECT @q = REPLACE(REPLACE(@q, '$join$', ''), '$group$', 'dbo.ff_GetWordNum(@tk, dbo.ff_Inlist2(tk, @tk))')
				ELSE SELECT @q = REPLACE(REPLACE(@q, '$join$', ' left join #$tk0 b on a.tk = b.tk and b.ma_so = @ma_so'), '$group$', 'b.tk0')

				EXEC sp_executesql @q,
					N'@ma_so varchar(33), @no_co numeric(1, 0), @khong_am numeric(1, 0), @tk char(512), @lk_kn numeric(28, 6) output, @lk_kn_nt numeric(28, 6) output, @lk_kt numeric(28, 6) output, @lk_kt_nt numeric(28, 6) output',
					@ma_so, @no_co, @khong_am, @tk, @lk_kn OUTPUT, @lk_kn_nt OUTPUT, @lk_kt OUTPUT, @lk_kt_nt OUTPUT
			END
		END

		IF @kind = '2' AND @cong_no = 1 AND @dau_cuoi = 1 BEGIN
			SELECT @q = '
select @ky_nay = sum(case when @no_co = 1 then du_no else du_co end)
	, @ky_nay_nt = sum(case when @no_co = 1 then du_no_nt else du_co_nt end)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #BgCusts a $join$
			where type = ''DateFrom''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$, ma_kh
		) a

select @ky_truoc = sum(case when @no_co = 1 then du_no else du_co end)
	, @ky_truoc_nt = sum(case when @no_co = 1 then du_no_nt else du_co_nt end)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #BgCusts a $join$
			where type = ''DatePreviousBegin''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$, ma_kh
		) a
'
			IF @queryType = 1 SELECT @q = REPLACE(REPLACE(@q, '$join$', ''), '$group$', 'dbo.ff_GetWordNum(@tk, dbo.ff_Inlist2(tk, @tk))')
			ELSE SELECT @q = REPLACE(REPLACE(@q, '$join$', ' left join #$tk0 b on a.tk = b.tk and b.ma_so = @ma_so'), '$group$', 'b.tk0')

			EXEC sp_executesql @q,
				N'@ma_so varchar(33), @no_co numeric(1, 0), @khong_am numeric(1, 0), @tk char(512), @ky_nay numeric(28, 6) output, @ky_nay_nt numeric(28, 6) output, @ky_truoc numeric(28, 6) output, @ky_truoc_nt numeric(28, 6) output',
				@ma_so, @no_co, @khong_am, @tk, @ky_nay OUTPUT, @ky_nay_nt OUTPUT, @ky_truoc OUTPUT, @ky_truoc_nt OUTPUT

			IF @reportType = '2' BEGIN
				SELECT @q = '
select @lk_kn = sum(case when @no_co = 1 then du_no else du_co end)
	, @lk_kn_nt = sum(case when @no_co = 1 then du_no_nt else du_co_nt end)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #BgCusts a $join$
			where type = ''DateFrom0''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$, ma_kh
		) a

select @lk_kt = sum(case when @no_co = 1 then du_no else du_co end)
	, @lk_kt_nt = sum(case when @no_co = 1 then du_no_nt else du_co_nt end)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #BgCusts a $join$
			where type = ''DatePreviousBegin0''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$, ma_kh
		) a
'
				IF @queryType = 1 SELECT @q = REPLACE(REPLACE(@q, '$join$', ''), '$group$', 'dbo.ff_GetWordNum(@tk, dbo.ff_Inlist2(tk, @tk))')
				ELSE SELECT @q = REPLACE(REPLACE(@q, '$join$', ' left join #$tk0 b on a.tk = b.tk and b.ma_so = @ma_so'), '$group$', 'b.tk0')

				EXEC sp_executesql @q,
					N'@ma_so varchar(33), @no_co numeric(1, 0), @khong_am numeric(1, 0), @tk char(512), @lk_kn numeric(28, 6) output, @lk_kn_nt numeric(28, 6) output, @lk_kt numeric(28, 6) output, @lk_kt_nt numeric(28, 6) output',
					@ma_so, @no_co, @khong_am, @tk, @lk_kn OUTPUT, @lk_kn_nt OUTPUT, @lk_kt OUTPUT, @lk_kt_nt OUTPUT
			END
		END

		IF @kind = '2' AND @cong_no = 1 AND @dau_cuoi = 2 BEGIN
			SELECT @q = '
select @ky_nay = sum(case when @no_co = 1 then du_no else du_co end)
	, @ky_nay_nt = sum(case when @no_co = 1 then du_no_nt else du_co_nt end)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #EdCusts a $join$
			where type = ''DateTo''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$, ma_kh
		) a

select @ky_truoc = sum(case when @no_co = 1 then du_no else du_co end)
	, @ky_truoc_nt = sum(case when @no_co = 1 then du_no_nt else du_co_nt end)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #EdCusts a $join$
			where type = ''DatePreviousEnd''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$, ma_kh
		) a
'
			IF @queryType = 1 SELECT @q = REPLACE(REPLACE(@q, '$join$', ''), '$group$', 'dbo.ff_GetWordNum(@tk, dbo.ff_Inlist2(tk, @tk))')
			ELSE SELECT @q = REPLACE(REPLACE(@q, '$join$', ' left join #$tk0 b on a.tk = b.tk and b.ma_so = @ma_so'), '$group$', 'b.tk0')

			EXEC sp_executesql @q,
				N'@ma_so varchar(33), @no_co numeric(1, 0), @khong_am numeric(1, 0), @tk char(512), @ky_nay numeric(28, 6) output, @ky_nay_nt numeric(28, 6) output, @ky_truoc numeric(28, 6) output, @ky_truoc_nt numeric(28, 6) output',
				@ma_so, @no_co, @khong_am, @tk, @ky_nay OUTPUT, @ky_nay_nt OUTPUT, @ky_truoc OUTPUT, @ky_truoc_nt OUTPUT

			IF @reportType = '2' BEGIN
				SELECT @q = '
select @lk_kn = sum(case when @no_co = 1 then du_no else du_co end)
	, @lk_kn_nt = sum(case when @no_co = 1 then du_no_nt else du_co_nt end)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #EdCusts a $join$
			where type = ''DateTo''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$, ma_kh
		) a

select @lk_kt = sum(case when @no_co = 1 then du_no else du_co end)
	, @lk_kt_nt = sum(case when @no_co = 1 then du_no_nt else du_co_nt end)
	from (
		select case when sum(du_no) - sum(du_co) >= 0 then sum(du_no) - sum(du_co) else 0 end as du_no
			, case when sum(du_co) - sum(du_no) >= 0 then sum(du_co) - sum(du_no) else 0 end as du_co
			, case when sum(du_no_nt) - sum(du_co_nt) >= 0 then sum(du_no_nt) - sum(du_co_nt) else 0 end as du_no_nt
			, case when sum(du_co_nt) - sum(du_no_nt) >= 0 then sum(du_co_nt) - sum(du_no_nt) else 0 end as du_co_nt
			from #EdCusts a $join$
			where type = ''DatePreviousEnd''' + CASE WHEN @tk <> '' THEN ' and ' + @$tk ELSE '' END + '
			group by $group$, ma_kh
		) a
'
				IF @queryType = 1 SELECT @q = REPLACE(REPLACE(@q, '$join$', ''), '$group$', 'dbo.ff_GetWordNum(@tk, dbo.ff_Inlist2(tk, @tk))')
				ELSE SELECT @q = REPLACE(REPLACE(@q, '$join$', ' left join #$tk0 b on a.tk = b.tk and b.ma_so = @ma_so'), '$group$', 'b.tk0')

				EXEC sp_executesql @q,
					N'@ma_so varchar(33), @no_co numeric(1, 0), @khong_am numeric(1, 0), @tk char(512), @lk_kn numeric(28, 6) output, @lk_kn_nt numeric(28, 6) output, @lk_kt numeric(28, 6) output, @lk_kt_nt numeric(28, 6) output',
					@ma_so, @no_co, @khong_am, @tk, @lk_kn OUTPUT, @lk_kn_nt OUTPUT, @lk_kt OUTPUT, @lk_kt_nt OUTPUT
			END
		END

		UPDATE #report SET Ky_nay = @ky_nay , Ky_truoc = @ky_truoc, Ky_naynt = @ky_nay_nt , Ky_truocnt = @ky_truoc_nt
				, Lk_kn = @lk_kn , Lk_kt = @lk_kt, Lk_kn_nt = @lk_kn_nt , Lk_kt_nt = @lk_kt_nt
			WHERE Ma_so = @ma_so
Next:
		FETCH NEXT FROM Cal_Cur INTO @kind, @ma_so, @cong_no, @no_co, @dau_cuoi, @khong_am, @tk, @tk_du, @xType
	END
	CLOSE Cal_Cur
	DEALLOCATE Cal_Cur

	UPDATE #report SET ky_nay = 0 WHERE ky_nay IS NULL
	UPDATE #report SET Ky_truoc = 0 WHERE Ky_truoc IS NULL
	UPDATE #report SET Ky_naynt = 0 WHERE ky_naynt IS NULL
	UPDATE #report SET Ky_truocnt = 0 WHERE Ky_truocnt IS NULL
	UPDATE #report SET Lk_kn = 0 WHERE Lk_kn IS NULL
	UPDATE #report SET Lk_kt = 0 WHERE Lk_kt IS NULL
	UPDATE #report SET Lk_kn_nt = 0 WHERE Lk_kn_nt IS NULL
	UPDATE #report SET Lk_kt_nt = 0 WHERE Lk_kt_nt IS NULL
	UPDATE #report SET ky_nay = - ky_nay, ky_truoc = - ky_truoc, ky_naynt = - ky_naynt, ky_truocnt = - ky_truocnt
		, Lk_kn = - Lk_kn, Lk_kt = - Lk_kt, Lk_kn_nt = - Lk_kn_nt, Lk_kt_nt = -  Lk_kt_nt
		WHERE dau = 0

	-- Formula
	DECLARE @formulaField VARCHAR(128)
	SELECT @formulaField = 'ky_nay, ky_naynt, ky_truoc, ky_truocnt' + CASE WHEN @reportType = '2' THEN ', lk_kn, lk_kn_nt, lk_kt, lk_kt_nt' ElSE '' END
	SELECT ma_so, cach_tinh, IDENTITY (INT, 1, 1) AS id INTO #tableFormula FROM #report WHERE Cach_tinh <> '' AND kind = '0' ORDER BY ids
	EXEC dbo.FastBusiness$Report$ConvertFormula '#tableFormula', '#report', @userID, @admin, DEFAULT, DEFAULT, @formulaField
	--
	IF @resultTable <> '' BEGIN
		DECLARE @resultName VARCHAR(32)
		SELECT @resultName = CASE WHEN EXISTS(SELECT 1 FROM tempdb.sys.columns WHERE OBJECT_ID = OBJECT_ID('tempdb..' + @resultTable) AND name = 'chi_tieu') THEN ', chi_tieu, chi_tieu2' ELSE '' END
		SELECT @q = 'insert into ' + @resultTable + ' (ma_so, tien_nt, tien' + @resultName + ') select ma_so, ky_naynt, ky_nay' + @resultName + ' from #report where in_ck = 1 and (ky_naynt <> 0 or ky_nay <> 0) order by stt'
		EXEC sp_executesql @q
	END ELSE BEGIN
		SELECT chi_tieu, chi_tieu2, ma_so, ma_so_in, thuyet_minh, ky_naynt, ky_truocnt, lk_kn_nt, lk_kt_nt, ky_nay, ky_truoc, lk_kn, lk_kt, bold, in_ck, stt AS sysorder, in_ck AS sysprint, CASE WHEN cach_tinh = '' THEN 1 ELSE 0 END AS systotal FROM #report ORDER BY Stt
	END
	SET NOCOUNT OFF
	SET ANSI_NULLS ON
END

GO

declare @0 datetime,@1 datetime,@2 datetime,@3 datetime,@4 varchar(3),@5 varchar(11),@6 varchar(3),@7 datetime,@8 varchar(1),@9 varchar(2)
select @0='20260101 00:00:00',@1='20260930 00:00:00',@2='20250101 00:00:00',@3='20250930 00:00:00',@4= '37',@5= 'V20GLTC605',@6= '10',@7=null,@8= '',@9= '0'

declare @reportType char(1), @ky int, @nam int, @baseCurrency varchar(32), @$quarter nvarchar(16), @$year nvarchar(16), @$year1 nvarchar(16), @$dFrom nvarchar(16), @$dTo nvarchar(16), @$value nvarchar(128), @$value2 nvarchar(128), @$value3 nvarchar(128), @$value4 nvarchar(128), @$value5 nvarchar(128), @$value6 nvarchar(128)
declare @$fiscalYear nvarchar(32), @$fiscalYear1 nvarchar(32), @$fiscalYear2 varchar(32), @$first bit, @$biFiscalYear nvarchar(128), @$biFiscalYear2 nvarchar(128), @$biFiscalQuarterYear nvarchar(128), @$biFiscalQuarterYear2 nvarchar(128)
declare @$biQuater nvarchar(128), @$biQuater2 nvarchar(128), @$biYear nvarchar(128), @$biYear2 nvarchar(128), @$biDFrom nvarchar(128), @$biDFrom2nvarchar(128), @$biDTo nvarchar(128), @$biDTo2 nvarchar(128),
  @$biValueDFrom smalldatetime, @$biValueDTo smalldatetime, @$biValueQuater int, @$biValueYear int, @$nt nvarchar(33), @$nt2 nvarchar(33),
  @$biAccu nvarchar(128), @$biAccu2 nvarchar(128), @$biAccu3 nvarchar(128), @$biAccu4 nvarchar(128), @$biAccuFC nvarchar(128), @$biAccuFC2 nvarchar(128), @$biAccuFC3 nvarchar(128), @$biAccuFC4 nvarchar(128)
  , @$biCurrentPeriod nvarchar(128), @$biCurrentPeriod2 nvarchar(128), @$biPreviousPeriod nvarchar(128), @$biPreviousPeriod2 nvarchar(128), @$biCurrentYear nvarchar(128), @$biCurrentYear2 nvarchar(128), @$biPreviousYear nvarchar(128), @$biPreviousYear2 nvarchar(128)
select @$biValueDFrom = @0, @$biValueDTo = @1, @$biValueQuater = @ky, @$biValueYear = @nam,
  @$biQuater = N'Quý', @$biQuater2 = 'Quarter', @$biYear = N'Năm', @$biYear2 = 'Year', @$biDFrom = N'Từ ngày', @$biDFrom2 = N'Date from', @$biDTo = N'đến ngày', @$biDTo2 = N'to',
  @$biCurrentPeriod = N'Kỳ này', @$biCurrentPeriod2 = N'Current Period', @$biPreviousPeriod = N'Kỳ trước', @$biPreviousPeriod2 = N'Previous Period',
  @$biCurrentYear = N'Năm này', @$biCurrentYear2 = N'Current Year', @$biPreviousYear = N'Năm trước', @$biPreviousYear2 = N'Previous Year',
  @$biAccu = N'Lũy kế từ đầu năm đến cuối quý này', @$biAccu2 = N'Accu. from Beginning of Year', @$biAccu3 = N'Lũy kế từ đầu năm đến cuối kỳ này', @$biAccu4 = N'Accu. from Beginning of Year',
  @$biAccuFC = N'Lũy kế từ đầu năm đến cuối quý (nt)', @$biAccuFC2 = N'YTD Accu. (FC)', @$biAccuFC3 = N'Lk từ đầu năm đến cuối kỳ (nt)', @$biAccuFC4 = N'YTD Accu. (FC)', @$nt = N'(nt)', @$nt2 = N'(FC)',
  @$biFiscalQuarterYear = N'niên độ', @$biFiscalQuarterYear2 = N'Fiscal Year', @$biFiscalYear = N'Niên độ', @$biFiscalYear2 = N'Fiscal Year'

select @baseCurrency = isnull(rtrim(val), '') from options where name = 'm_ma_nt0'
select @$quarter = case when 'V' = 'v' then N'Quý ' else N'Quarter ' end
select @$year = case when 'V' = 'v' then N'Năm ' else N'Year ' end
select @$year1 = case when 'V' = 'v' then N'năm ' else N'Year ' end
select @$dFrom = case when 'V' = 'v' then N'Từ ngày ' else N'Date from ' end
select @$dTo = case when 'V' = 'v' then N'đến ngày ' else N'to ' end
select @$value = case when 'V' = 'v' then N'Kỳ này' else N'Current Period' end
select @$value2 = case when 'V' = 'v' then N'Kỳ trước' else N'Previous Period' end
select @$value3 = case when 'V' = 'v' then N'Năm nay' else N'Current Year' end
select @$value4 = case when 'V' = 'v' then N'Năm trước' else N'Previous Year' end
select @$value5 = case when 'V' = 'v' then N'Lũy kế từ đầu năm đến cuối kỳ này' else N'Accu. from Beginning of Year' end
select @$value6 = case when 'V' = 'v' then N'Lũy kế từ đầu năm đến cuối quý này' else N'Accu. from Beginning of Year' end
select @$fiscalYear = case when 'V' = 'v' then N'Niên độ ' else N'Fiscal Year ' end
select @$fiscalYear1 = case when 'V' = 'v' then N'niên độ ' else N'Fiscal Year ' end

select @reportType = case when @6 = '10' or @6 = '30' then '1' else '2' end

create table #tmp (quy int, nam int)
insert into #tmp exec FastBusiness$Report$GetQuaterAndYear @0, @1
select @ky = quy, @nam = nam from #tmp
drop table #tmp

if not exists (select 1 from dmstt where day(ngay_dn2) = 1 and month(ngay_dn2) = 1) begin
  select @$fiscalYear2 = case when (@nam <> 0) then rtrim(@nam) + ' - ' + rtrim(@nam + 1) else '' end, @$first = 1
end

select @0 as date_from, @1 as date_to, @ky as quarter, @nam as year, isnull(@$fiscalYear2, '') as fiscal_year, isnull(@$first, 0) as first,
       case when (@ky <> 0 and @nam <> 0) then @$quarter + rtrim(@ky) + ' ' + case when @$first = 1 then @$fiscalYear1 + @$fiscalYear2 else @$year1 + rtrim(@nam) end else (case when @nam <> 0 then case when @$first = 1 then @$fiscalYear + @$fiscalYear2 else @$year + rtrim(@nam) end else @$dFrom + convert(varchar(10), @0, 103) + ' ' + @$dTo + convert(varchar(10), @1, 103) end) end as subTitle,
       case when @ky <> 0 then @$quarter + rtrim(@ky) else @$value end as value,
       case when (@ky = 0 and @nam = 0) then @$value else @$value3 end as ky_nay,
       case when (@ky = 0 and @nam = 0) then @$value2 else @$value4 end as ky_truoc,
       case when @ky = 0 then @$value5 else @$value6 end as lk_ky,
       case when @nam <> 0 then @nam else year(@0) end as nam_tc, @baseCurrency as ma_nt,

       case when (@ky <> 0 and @nam <> 0) then @$biQuater else '' end as h_quy,
       case when (@ky <> 0 and @nam <> 0) then @$biQuater2 else '' end as [2_h_quy],
       case when (@ky <> 0 and @nam <> 0) then ' (' else '' end as h_quy_mo,
       case when (@ky <> 0 and @nam <> 0) then ') ' else '' end as h_quy_dong,
       case when (@ky <> 0 and @nam <> 0) then @ky else null end as v_quy,

       case when (@ky <> 0 and @nam <> 0) then ' ' + case when @$first = 1 then @$biFiscalQuarterYear else @$biYear end else '' end as h_nam_quy,
       case when (@ky <> 0 and @nam <> 0) then case when @$first = 1 then @$biFiscalQuarterYear2 else @$biYear2 end else '' end as [2_h_nam_quy],
       case when (@ky <> 0 and @nam <> 0) then ' (' else '' end as h_nam_quy_mo,
       case when (@ky <> 0 and @nam <> 0) then ') ' else '' end as h_nam_quy_dong,
       case when (@ky <> 0 and @nam <> 0) then case when @$first = 1 then @$fiscalYear2 else rtrim(@nam) end else null end as v_nam_quy,

       case when (@ky = 0 and @nam <> 0) then case when @$first = 1 then @$biFiscalYear else @$biYear end else '' end as h_nam,
       case when (@ky = 0 and @nam <> 0) then case when @$first = 1 then @$biFiscalYear2 else @$biYear2 end else '' end as [2_h_nam],
       case when (@ky = 0 and @nam <> 0) then ' (' else '' end as h_nam_mo,
       case when (@ky = 0 and @nam <> 0) then ') ' else '' end as h_nam_dong,
       case when (@ky = 0 and @nam <> 0) then case when @$first = 1 then @$fiscalYear2 else rtrim(@nam) end else null end as v_nam,

       case when (@ky = 0 and @nam = 0) then @$biDFrom else '' end as h_tu_ngay,
       case when (@ky = 0 and @nam = 0) then @$biDFrom2 else '' end as [2_h_tu_ngay],
       case when (@ky = 0 and @nam = 0) then ' (' else '' end as h_tu_ngay_mo,
       case when (@ky = 0 and @nam = 0) then ') ' else '' end as h_tu_ngay_dong,
       case when (@ky = 0 and @nam = 0) then @0 else null end as v_tu_ngay,

       case when (@ky = 0 and @nam = 0) then ' ' + @$biDTo else '' end as h_den_ngay,
       case when (@ky = 0 and @nam = 0) then @$biDTo2 else '' end as [2_h_den_ngay],
       case when (@ky = 0 and @nam = 0) then ' (' else '' end as h_den_ngay_mo,
       case when (@ky = 0 and @nam = 0) then ') ' else '' end as h_den_ngay_dong,
       case when (@ky = 0 and @nam = 0) then @1 else null end as v_den_ngay,

       case when (@ky <> 0) then @$biQuater + rtrim(@ky) else @$biCurrentPeriod end as f_quy,
       case when (@ky <> 0) then @$biQuater2 + rtrim(@ky) else @$biCurrentPeriod2 end as f_quy2,
       case when (@ky <> 0) then @$biQuater + rtrim(@ky) + ' ' + @$nt else @$biCurrentPeriod + ' ' + @$nt end as f_quy_nt,
       case when (@ky <> 0) then @$biQuater2 + rtrim(@ky) + ' ' + @$nt2 else @$biCurrentPeriod2 + ' ' + @$nt2 end as f_quy_nt2,

       case when (@ky <> 0) then @$biAccu else @$biAccu3 end as f_luy_ke,
       case when (@ky <> 0) then @$biAccu2 else @$biAccu4 end as f_luy_ke2,
       case when (@ky <> 0) then @$biAccuFC else @$biAccuFC3 end as f_luy_ke_nt,
       case when (@ky <> 0) then @$biAccuFC2 else @$biAccuFC4 end as f_luy_ke_nt2,

       case when (@ky = 0 and @nam = 0) then @$biCurrentPeriod else @$biCurrentYear end as f_ky_nay,
       case when (@ky = 0 and @nam = 0) then @$biCurrentPeriod2 else @$biCurrentYear2 end as f_ky_nay2,
       case when (@ky = 0 and @nam = 0) then @$biCurrentPeriod + ' ' + @$nt else @$biCurrentYear + ' ' + @$nt end as f_ky_nay_nt,
       case when (@ky = 0 and @nam = 0) then @$biCurrentPeriod2 + ' ' + @$nt2 else @$biCurrentYear2 + ' ' + @$nt2 end as f_ky_nay_nt2,

       case when (@ky = 0 and @nam = 0) then @$biPreviousPeriod else @$biPreviousYear end as f_ky_truoc,
       case when (@ky = 0 and @nam = 0) then @$biPreviousPeriod2 else @$biPreviousYear2 end as f_ky_truoc2,
       case when (@ky = 0 and @nam = 0) then @$biPreviousPeriod + ' ' + @$nt else @$biPreviousYear + ' ' + @$nt end as f_ky_truoc_nt,
       case when (@ky = 0 and @nam = 0) then @$biPreviousPeriod2 + ' ' + @$nt2 else @$biPreviousYear2 + ' ' + @$nt2 end as f_ky_truoc_nt2,

       h_line1, h_line12, h_line2, h_line22, h_line3, h_line32, h_line4, h_line42, h_line5, h_line52 from v20dmmaubc where ma_maubc = 'V20GLTC6' and form = @5
exec rs_rptInDirectCashflow @0, @1, @2, @3, @4, @5, @reportType, 'V', 18, 1

        select @9 as rptMarginLeft
        , replace(cast(char(255) + isnull(convert(varchar(8), @7, 112), '') + char(252) + @8 + char(252) + lower(replace(newid(),'-','')) + char(255) as nvarchar(4000)), ' ', char(251)) as queryParameter
      
