/* =====================================================================
   10_TaiHienLoiSQL.sql — Tự tay gây ra các lỗi SQL hay gặp và xem thông báo thật
   ---------------------------------------------------------------------
   Chạy trên FBOSP2422_App (hoặc database thử bất kỳ). Script chỉ dùng bảng tạm
   (#...) và procedure tạm (#...), chỉ ĐỌC bảng thật dmkh — không ghi gì vào database.

   Cách dùng: chạy cả file (F5) rồi đọc tab Messages. Mỗi mục in ra:
       === Lxx. <tên lỗi>
          -> Msg <số lỗi>: <thông báo gốc của SQL Server>
          Sửa: <cách sửa>
   Mục Lxx là lỗi có thông báo; mục Sxx là lỗi "im lặng" (không báo gì nhưng ra sai số),
   xem kết quả ở tab Results. Muốn xem kỹ một mục, bôi đen đoạn đó rồi chạy riêng.
   Đối chiếu với tài liệu: Buổi 9, mục "Lỗi SQL hay gặp".
   ===================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT OFF;
IF OBJECT_ID('tempdb..#vt') IS NOT NULL DROP TABLE #vt;
IF OBJECT_ID('tempdb..#hai') IS NOT NULL DROP TABLE #hai;
IF OBJECT_ID('tempdb..#ct') IS NOT NULL DROP TABLE #ct;
CREATE TABLE #vt (ma_vt varchar(16) NOT NULL PRIMARY KEY, ten_vt nvarchar(128) NULL, so_luong numeric(16, 3) NULL);
INSERT INTO #vt VALUES ('001', N'Gạo tám thơm', 10), ('002', N'Đường kính trắng', NULL), ('A01', N'Bột mì', 5);
CREATE TABLE #hai (ma varchar(16));
INSERT INTO #hai VALUES ('KH01'), ('KH02');
CREATE TABLE #ct (so_ct varchar(12), ngay_tao datetime, tien numeric(16, 2));
INSERT INTO #ct VALUES ('PT01', '20260131 00:00', 100), ('PT02', '20260131 10:30', 200), ('PT03', '20260201 08:00', 300);
GO

/* ---------------- PHẦN 1. Lỗi có thông báo (Msg) ---------------- */

PRINT N'=== L01. Chia cho 0';
BEGIN TRY
    EXEC (N'SELECT 1000 / 0 AS don_gia');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: tien / NULLIF(so_luong, 0)  (ra NULL thay vì lỗi), rồi ISNULL(..., 0) nếu cần số 0.';
GO

PRINT N'=== L02. So cột chữ với số: SQL đổi cả cột sang số và gặp mã có chữ';
BEGIN TRY
    EXEC (N'SELECT * FROM #vt WHERE ma_vt = 1');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: mã là chữ thì so với chữ: WHERE ma_vt = ''001''. Lỗi chỉ hiện khi bảng có mã chứa chữ, nên chạy thử ở dự án này thì được mà sang dự án khác thì lỗi.';
GO

PRINT N'=== L03. Đổi chuỗi có dấu phẩy ngăn cách nghìn sang số';
BEGIN TRY
    EXEC (N'SELECT CONVERT(numeric(18, 2), N''1,234.50'')');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: bỏ dấu ngăn cách trước khi đổi: CONVERT(numeric(18, 2), REPLACE(@s, '','', '''')). Dữ liệu import từ Excel hay gặp lỗi này.';
GO

PRINT N'=== L04. Ngày viết dạng dd/mm/yyyy trong khi SQL đọc theo mm/dd/yyyy';
BEGIN TRY
    EXEC (N'SET DATEFORMAT mdy; SELECT CONVERT(datetime, ''31/01/2026'')');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: viết ngày dạng ''yyyymmdd'' (''20260131'') — đúng với mọi cấu hình; chuỗi dd/mm/yyyy thì dùng CONVERT(datetime, @s, 103).';
PRINT N'   Nguy hiểm hơn: ''05/01/2026'' không báo lỗi mà thành ngày 1 tháng 5.';
GO

PRINT N'=== L05. Chuỗi dài hơn độ dài cột';
BEGIN TRY
    EXEC (N'INSERT INTO #vt (ma_vt, ten_vt) VALUES (''VATTU-QUA-DAI-0001'', N''Thử'')');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: kiểm tra độ dài cột (sp_help ''dmvt'' hoặc Bcode tab Table) rồi cắt bằng LEFT() hoặc báo người dùng; bảng tạm khai báo đủ dài.';
PRINT N'   Database ở compatibility level 100 chỉ báo "would be truncated", không nói cột nào — so từng cột với độ dài dữ liệu.';
GO

PRINT N'=== L06. Số vượt quá độ chính xác của kiểu numeric';
BEGIN TRY
    EXEC (N'DECLARE @tl numeric(5, 2); SET @tl = 1234.5; SELECT @tl');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: numeric(5, 2) chỉ chứa tới 999.99. Khai báo biến / cột bảng tạm theo đúng kiểu cột Fast: tiền, số lượng, giá đều là numeric(19, 4).';
GO

PRINT N'=== L07. Truy vấn con trả về nhiều hơn 1 dòng';
BEGIN TRY
    EXEC (N'SELECT (SELECT ma FROM #hai) AS ma_kh');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: thêm điều kiện để chỉ còn 1 dòng, hoặc TOP 1 ... ORDER BY rõ ràng, hoặc đổi sang JOIN / EXISTS. Thường do dữ liệu có mã trùng mà lúc viết không lường trước.';
GO

PRINT N'=== L08. Dùng bí danh bảng không có trong FROM';
BEGIN TRY
    EXEC (N'SELECT a.ma_kh FROM dmkh k');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: dùng đúng bí danh đã đặt (k.ma_kh). Hay gặp khi chép đoạn WHERE từ procedure khác sang.';
GO

PRINT N'=== L09. Cột trong SELECT không nằm trong GROUP BY';
BEGIN TRY
    EXEC (N'SELECT ma_vt, ten_vt, SUM(so_luong) FROM #vt GROUP BY ma_vt');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: thêm ten_vt vào GROUP BY, hoặc lấy MAX(ten_vt), hoặc GROUP BY mã trước rồi JOIN danh mục để lấy tên.';
GO

PRINT N'=== L10. SELECT DISTINCT nhưng ORDER BY cột không có trong SELECT';
BEGIN TRY
    EXEC (N'SELECT DISTINCT ma_vt FROM #vt ORDER BY ten_vt');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: đưa ten_vt vào SELECT, hoặc dùng GROUP BY ma_vt ORDER BY MAX(ten_vt).';
GO

PRINT N'=== L11. Thêm cột rồi dùng ngay cột đó trong cùng một batch';
BEGIN TRY
    EXEC (N'ALTER TABLE #vt ADD gia_ban numeric(16, 2) NULL; UPDATE #vt SET gia_ban = 0');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: tách thành 2 batch bằng GO (ALTER TABLE ... GO rồi UPDATE). Trong procedure: chạy UPDATE bằng EXEC(...) riêng.';
GO
IF COL_LENGTH('tempdb..#vt', 'gia_ban') IS NOT NULL ALTER TABLE #vt DROP COLUMN gia_ban;
GO

PRINT N'=== L12. Bảng tạm tạo trong EXEC(...) không thấy được ở ngoài';
BEGIN TRY
    EXEC (N'EXEC (N''SELECT 1 AS a INTO #tam_ben_trong''); SELECT * FROM #tam_ben_trong');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: CREATE TABLE #tam ở ngoài trước, trong chuỗi động chỉ INSERT INTO #tam ... (FastBusiness$Partition$Execute cũng theo cách này).';
GO

PRINT N'=== L13. Tạo bảng tạm 2 lần cùng tên';
BEGIN TRY
    EXEC (N'CREATE TABLE #trung (a int); CREATE TABLE #trung (a int)');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: đầu đoạn code thêm IF OBJECT_ID(''tempdb..#trung'') IS NOT NULL DROP TABLE #trung. Hay gặp khi chạy lại đoạn test trong cùng tab SQL Query.';
GO

PRINT N'=== L14. Trùng khoá chính';
BEGIN TRY
    EXEC (N'INSERT INTO #vt (ma_vt, ten_vt) VALUES (''001'', N''Gạo trùng mã'')');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: INSERT ... WHERE NOT EXISTS (SELECT 1 FROM bảng WHERE khoá = ...). Import danh mục: lọc trùng trong chính file Excel trước.';
GO

PRINT N'=== L15. Ghi NULL vào cột NOT NULL';
BEGIN TRY
    EXEC (N'INSERT INTO #vt (ma_vt, ten_vt) VALUES (NULL, N''Không có mã'')');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: ISNULL(cột, '''') / ISNULL(cột, 0) khi INSERT; với bảng Fast nhớ điền đủ các cột hệ thống (status, datetime0, user_id0...).';
GO

PRINT N'=== L16. INSERT không ghi tên cột, số cột không khớp bảng';
BEGIN TRY
    EXEC (N'INSERT INTO #vt SELECT ''009'', N''Muối''');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: luôn ghi danh sách cột: INSERT INTO #vt (ma_vt, ten_vt) SELECT ... Khi Fast thêm cột vào bảng ở bản mới, câu INSERT không ghi tên cột sẽ lỗi ngay.';
GO

PRINT N'=== L17. Biến bên ngoài không dùng được trong chuỗi SQL động';
BEGIN TRY
    DECLARE @Unit varchar(8);
    SET @Unit = '01';
    EXEC (N'SELECT TOP 1 ma_kh FROM dmkh WHERE ma_dvcs = @Unit');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: truyền biến bằng sp_executesql N''... WHERE ma_dvcs = @Unit'', N''@Unit varchar(8)'', @Unit; hoặc nối giá trị vào chuỗi (nhớ nháy đơn).';
GO

PRINT N'=== L18. Dữ liệu có dấu nháy đơn làm vỡ chuỗi SQL động';
BEGIN TRY
    DECLARE @ten nvarchar(128), @sql nvarchar(max);
    SET @ten = N'Công ty O''Neil';
    SET @sql = N'SELECT * FROM dmkh WHERE ten_kh = N''' + @ten + N'''';
    PRINT N'   Chuỗi đã ghép: ' + @sql;
    EXEC (@sql);
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: nhân đôi dấu nháy trước khi ghép: REPLACE(@ten, '''''''', ''''''''''''), hoặc dùng sp_executesql có tham số.';
GO

PRINT N'=== L19. Xung đột collation giữa bảng tạm và bảng thật';
BEGIN TRY
    EXEC (N'CREATE TABLE #kh_khac (ma_kh varchar(16) COLLATE Vietnamese_CI_AS);
            SELECT k.ma_kh FROM dmkh k JOIN #kh_khac t ON t.ma_kh = k.ma_kh');
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: khi tạo bảng tạm ghi COLLATE DATABASE_DEFAULT cho cột chữ, hoặc tạo bằng SELECT TOP 0 ... INTO #t FROM bảng thật.';
PRINT N'   Lỗi chỉ hiện khi collation của server (tempdb) khác collation database — máy mình chạy được, máy khách lỗi.';
GO

IF OBJECT_ID('tempdb..#p_hai_tham_so') IS NOT NULL DROP PROCEDURE #p_hai_tham_so;
GO
CREATE PROCEDURE #p_hai_tham_so @Unit varchar(8), @Year int AS SELECT @Unit AS ma_dvcs, @Year AS nam;
GO
PRINT N'=== L20. Gọi procedure thừa tham số';
BEGIN TRY
    EXEC #p_hai_tham_so '01', 2026, 'thừa';
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: Filter đã thêm tham số nhưng procedure trên database vẫn là bản cũ — chạy script procedure mới, kiểm tra bằng sys.parameters.';
GO

PRINT N'=== L21. Gọi procedure thiếu tham số không có giá trị mặc định';
BEGIN TRY
    EXEC #p_hai_tham_so '01';
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: truyền đủ tham số, hoặc khi thêm tham số mới vào procedure đang dùng thì cho giá trị mặc định (@Moi int = 0) để chỗ gọi cũ vẫn chạy.';
GO

IF OBJECT_ID('tempdb..#p_quen_commit') IS NOT NULL DROP PROCEDURE #p_quen_commit;
GO
CREATE PROCEDURE #p_quen_commit AS
BEGIN
    BEGIN TRAN;
    UPDATE #vt SET so_luong = so_luong WHERE 1 = 0;
    -- quên COMMIT
END
GO
PRINT N'=== L22. Procedure mở transaction mà không COMMIT / ROLLBACK';
BEGIN TRY
    EXEC #p_quen_commit;
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   @@TRANCOUNT sau lỗi = ' + CONVERT(nvarchar(10), @@TRANCOUNT) + N' (transaction vẫn đang mở, đang giữ khoá)';
IF @@TRANCOUNT > 0 ROLLBACK;
PRINT N'   Sửa: mọi nhánh của procedure (kể cả RETURN giữa chừng, khối CATCH) phải COMMIT hoặc ROLLBACK.';
GO

PRINT N'=== L23. Transaction bị hỏng khi bật XACT_ABORT: lỗi rồi vẫn cố ghi tiếp';
SET XACT_ABORT ON;
BEGIN TRAN;
BEGIN TRY
    SELECT 1 / 0;
END TRY
BEGIN CATCH
    PRINT N'   Lỗi đầu tiên: ' + ERROR_MESSAGE() + N'  | XACT_STATE() = ' + CONVERT(nvarchar(10), XACT_STATE());
    BEGIN TRY
        INSERT INTO #hai (ma) VALUES ('GHI_LOG');
    END TRY
    BEGIN CATCH
        PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
    END CATCH
END CATCH
IF @@TRANCOUNT > 0 ROLLBACK;
SET XACT_ABORT OFF;
PRINT N'   Sửa: trong CATCH, nếu XACT_STATE() = -1 thì ROLLBACK trước rồi mới ghi log. SET XACT_ABORT ON còn hiệu lực tới hết phiên kết nối — tắt lại khi xong.';
GO

PRINT N'=== L24. Khai báo cursor trùng tên do lần chạy trước chưa DEALLOCATE';
BEGIN TRY
    DECLARE c_vt CURSOR GLOBAL FOR SELECT ma_vt FROM #vt;
    DECLARE c_vt CURSOR GLOBAL FOR SELECT ma_vt FROM #vt;
END TRY
BEGIN CATCH
    PRINT N'   -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
IF CURSOR_STATUS('global', 'c_vt') >= -1 DEALLOCATE c_vt;
PRINT N'   Sửa: khai báo DECLARE c CURSOR LOCAL FAST_FORWARD FOR ...; luôn CLOSE + DEALLOCATE. Tốt hơn nữa: viết lại bằng câu UPDATE / INSERT ... SELECT theo tập.';
GO

PRINT N'=== L25. Hàm không có ở compatibility level 100 (database Fast)';
BEGIN TRY
    EXEC (N'SELECT TRY_CONVERT(int, ''abc'')');
END TRY
BEGIN CATCH
    PRINT N'   TRY_CONVERT  -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
BEGIN TRY
    EXEC (N'SELECT value FROM STRING_SPLIT(''331,3311'', '','')');
END TRY
BEGIN CATCH
    PRINT N'   STRING_SPLIT -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
BEGIN TRY
    EXEC (N'SELECT * FROM OPENJSON(''[1,2]'')');
END TRY
BEGIN CATCH
    PRINT N'   OPENJSON     -> Msg ' + CONVERT(nvarchar(10), ERROR_NUMBER()) + N': ' + ERROR_MESSAGE();
END CATCH
PRINT N'   Sửa: tách chuỗi bằng FastBusiness$Function$System$Split; thay TRY_CONVERT bằng CASE WHEN ISNUMERIC(...) = 1 ... hoặc TRY_CAST (kiểm tra server khách có chạy được không).';
GO

/* ---------------- PHẦN 2. Lỗi "im lặng": không báo gì nhưng ra sai ---------------- */

PRINT N'=== S01. Nối chuỗi với NULL: cả chuỗi SQL động thành NULL, EXEC không chạy gì và không báo lỗi';
DECLARE @Key nvarchar(max), @sql nvarchar(max);
SET @Key = NULL;                                   -- ví dụ tham số người dùng bỏ trống
SET @sql = N'SELECT ma_vt FROM #vt WHERE 1 = 1' + @Key;
SELECT N'S01' AS muc, CASE WHEN @sql IS NULL THEN N'@sql = NULL -> EXEC(@sql) không ra dòng nào, không báo lỗi' ELSE @sql END AS ket_qua;
EXEC (@sql);
-- Sửa: ISNULL(@Key, N'') trước khi nối; PRINT @sql để xem chuỗi thật trước khi EXEC.
GO

PRINT N'=== S02. NOT IN với danh sách có NULL: không ra dòng nào';
SELECT N'S02 NOT IN' AS muc, COUNT(*) AS so_dong FROM #vt WHERE ma_vt NOT IN (SELECT '001' UNION ALL SELECT NULL);
SELECT N'S02 NOT EXISTS' AS muc, COUNT(*) AS so_dong FROM #vt v
WHERE NOT EXISTS (SELECT 1 FROM (SELECT '001' AS ma UNION ALL SELECT NULL) x WHERE x.ma = v.ma_vt);
-- Sửa: dùng NOT EXISTS, hoặc lọc bỏ NULL trong truy vấn con (WHERE ma IS NOT NULL).
GO

PRINT N'=== S03. Điều kiện <> bỏ mất các dòng NULL';
SELECT N'S03 <> 10' AS muc, COUNT(*) AS so_dong FROM #vt WHERE so_luong <> 10;
SELECT N'S03 ISNULL' AS muc, COUNT(*) AS so_dong FROM #vt WHERE ISNULL(so_luong, 0) <> 10;
-- Dòng so_luong = NULL không thoả cả "= 10" lẫn "<> 10". Sửa: ISNULL(cột, 0) hoặc thêm OR cột IS NULL (nhớ bọc ngoặc).
GO

PRINT N'=== S04. Cộng / đếm với NULL';
SELECT N'S04' AS muc,
       SUM(so_luong)               AS sum_bo_qua_null,      -- 15: SUM bỏ qua NULL
       10 + NULL                   AS cong_null_ra_null,    -- NULL
       COUNT(*)                    AS dem_moi_dong,          -- 3
       COUNT(so_luong)             AS dem_bo_null,           -- 2
       AVG(so_luong)               AS tb_bo_null,            -- 7.5 chứ không phải 5
       (SELECT SUM(so_luong) FROM #vt WHERE 1 = 0) AS sum_khong_co_dong   -- NULL chứ không phải 0
FROM #vt;
-- Sửa: ISNULL(SUM(...), 0); nghĩ trước dòng NULL được tính hay không khi dùng COUNT / AVG.
GO

PRINT N'=== S05. Chia hai số nguyên ra số nguyên';
SELECT N'S05' AS muc, 1 / 2 AS so_nguyen, 1 / 2.0 AS so_thuc, 7 / 2 AS bay_chia_hai, CAST(7 AS numeric(16, 4)) / 2 AS dung;
-- Sửa: đổi một vế sang numeric trước khi chia (tỷ lệ %, đơn giá bình quân...).
GO

PRINT N'=== S06. ISNULL lấy kiểu dữ liệu của tham số đầu: giá trị thay thế bị cắt';
DECLARE @ma varchar(3);
SELECT N'S06' AS muc, ISNULL(@ma, 'TAT_CA') AS isnull_bi_cat, COALESCE(@ma, 'TAT_CA') AS coalesce_du;
-- Sửa: dùng COALESCE, hoặc khai báo biến đủ dài.
GO

PRINT N'=== S07. Quên tiền tố N: chữ tiếng Việt thành dấu ?';
SELECT N'S07' AS muc, 'Hà Nội - Đà Nẵng' AS thieu_N, N'Hà Nội - Đà Nẵng' AS co_N;
-- Sửa: chuỗi tiếng Việt luôn viết N'...', biến / cột chứa tiếng Việt phải là nvarchar. Chữ đã lưu thành ? thì không lấy lại được.
GO

PRINT N'=== S08. Chuỗi SQL động dài bị cắt ở 4000 ký tự';
DECLARE @s nvarchar(max);
SET @s = REPLICATE(N'x', 5000);
SELECT N'S08' AS muc, LEN(@s) AS replicate_thuong, LEN(REPLICATE(CAST(N'x' AS nvarchar(max)), 5000)) AS replicate_max;
-- Biến nvarchar(max) nhưng giá trị gán vào là nvarchar(4000) nên bị cắt trước. Chuỗi động dài: CAST phần đầu AS nvarchar(max)
-- rồi mới nối; khai báo biến nvarchar(4000) cũng bị cắt. PRINT chỉ in 4000 ký tự đầu — dùng SELECT để xem hết.
GO

PRINT N'=== S09. Lọc ngày đến hết ngày 31 trên cột có giờ';
SELECT N'S09 <= 20260131' AS muc, COUNT(*) AS so_dong FROM #ct WHERE ngay_tao BETWEEN '20260101' AND '20260131';
SELECT N'S09 < 20260201' AS muc, COUNT(*) AS so_dong FROM #ct WHERE ngay_tao >= '20260101' AND ngay_tao < '20260201';
-- Phiếu PT02 tạo lúc 10:30 ngày 31 bị mất. ngay_ct của chứng từ Fast không có giờ, nhưng datetime0 / datetime2 thì có.
GO

PRINT N'=== S10. LIKE: dấu _ là ký tự đại diện';
SELECT N'S10' AS muc, ma_vt FROM #vt WHERE ma_vt LIKE 'A_1%' OR ma_vt LIKE '0_1';
SELECT N'S10 sua' AS muc, COUNT(*) AS so_dong FROM #vt WHERE ma_vt LIKE '0[_]1';
-- Mã có dấu _ hoặc % thì bọc trong [ ]: LIKE 'TK[_]%'.
GO

PRINT N'=== S11. Khoảng trắng cuối chuỗi bị bỏ qua khi so sánh, khoảng trắng đầu thì không';
SELECT N'S11' AS muc,
       CASE WHEN 'KH01' = 'KH01   ' THEN N'bằng' ELSE N'khác' END AS khoang_trang_cuoi,
       CASE WHEN 'KH01' = '  KH01' THEN N'bằng' ELSE N'khác' END AS khoang_trang_dau,
       LEN('KH01   ') AS len_bo_cuoi, DATALENGTH('KH01   ') AS so_byte;
-- Mã import từ Excel có khoảng trắng đầu thì không khớp danh mục. Sửa: LTRIM(RTRIM()) khi import / khi so.
GO

PRINT N'=== S12. Kiểu float cộng dồn bị lệch';
DECLARE @f float, @n numeric(16, 2), @i int;
SELECT @f = 0, @n = 0, @i = 0;
WHILE @i < 10 BEGIN SELECT @f = @f + 0.1, @n = @n + 0.1, @i = @i + 1; END
SELECT N'S12' AS muc, CASE WHEN @f = 1 THEN N'float = 1' ELSE N'float <> 1' END AS ket_qua_float,
       CASE WHEN @n = 1 THEN N'numeric = 1' ELSE N'numeric <> 1' END AS ket_qua_numeric;
-- Tiền, số lượng luôn dùng numeric như cột Fast, không dùng float / real.
GO

PRINT N'=== S13. RAISERROR mức 10 không nhảy vào CATCH, code chạy tiếp';
BEGIN TRY
    RAISERROR (N'Kiểm tra không đạt nhưng mức 10 chỉ là thông báo', 10, 1);
    PRINT N'   ...dòng sau RAISERROR mức 10 vẫn chạy';
    RAISERROR (N'Mức 16 thì dừng', 16, 1);
    PRINT N'   (dòng này không chạy)';
END TRY
BEGIN CATCH
    PRINT N'   -> vào CATCH: ' + ERROR_MESSAGE();
END CATCH
-- Muốn chặn thì dùng mức 16, sau RAISERROR thêm RETURN.
GO

PRINT N'=== S14. @@ROWCOUNT bị lệnh khác ghi đè';
UPDATE #vt SET so_luong = so_luong WHERE ma_vt IN ('001', 'A01');
IF 1 = 1 PRINT N'   (một lệnh IF / PRINT xen giữa)';
SELECT N'S14' AS muc, @@ROWCOUNT AS rowcount_sau_lenh_xen;
-- Sửa: lưu ngay sau câu cần đếm: DECLARE @n int; UPDATE ...; SET @n = @@ROWCOUNT;
GO

DROP TABLE #vt; DROP TABLE #hai; DROP TABLE #ct;
IF OBJECT_ID('tempdb..#p_hai_tham_so') IS NOT NULL DROP PROCEDURE #p_hai_tham_so;
IF OBJECT_ID('tempdb..#p_quen_commit') IS NOT NULL DROP PROCEDURE #p_quen_commit;
PRINT N'=== Xong. @@TRANCOUNT = ' + CONVERT(nvarchar(10), @@TRANCOUNT) + N' (phải là 0)';
