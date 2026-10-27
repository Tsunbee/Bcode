/* =====================================================================================
   BÀI TẬP ĐÀO TẠO FAST + BCODE — ĐÁP ÁN
   Chạy sau 00_TaoDuLieuMau.sql, 01_PostHoaDonBan.sql và 02_PostPhieuNhap.sql; riêng các bài buổi 9 phải chạy sau 09_TinhHuongLoi.sql.
   Mỗi bài đều có lệnh USE trỏ đúng database. Bài sửa dữ liệu nằm trong BEGIN TRAN ... ROLLBACK, TRỪ các bài tạo / sửa object thật:
   3.2 (DROP bảng sao lưu), 5.2 (CREATE + INSERT), 8.2 và 9.1–9.4 (ALTER PROCEDURE), 9.5 (ALTER TABLE + UPDATE) — 99_XoaDuLieuMau.sql dọn lại.
   ===================================================================================== */


/* ######################## BUỔI 1 — LÀM QUEN HỆ THỐNG FAST VÀ BCODE ######################## */

-- ===== Bài 1.1 — Đếm số dòng của các bảng menu =====
USE FBOSP2422_Sys
GO
SELECT (SELECT COUNT(*) FROM wcommand) AS so_menu_web,
       (SELECT COUNT(*) FROM command)  AS so_dong_command
GO

-- ===== Bài 1.2 — Tìm menu theo tên trang web =====
USE FBOSP2422_Sys
GO
SELECT wmenu_id, wmenu_id0, bar, bar2, link, sysid
FROM wcommand
WHERE link LIKE 'inctpnd%'
GO

-- ===== Bài 1.3 — Tìm menu cha =====
USE FBOSP2422_Sys
GO
SELECT c.wmenu_id, c.bar, p.wmenu_id AS menu_cha, p.bar AS ten_menu_cha
FROM wcommand c
LEFT JOIN wcommand p ON p.wmenu_id = c.wmenu_id0
WHERE c.link LIKE 'inctpnd%'
GO

-- ===== Bài 1.4 — Một trang web dùng cho nhiều menu =====
USE FBOSP2422_Sys
GO
SELECT wmenu_id, bar, link, parameter, sysid
FROM wcommand
WHERE link LIKE 'arkh%'
ORDER BY wmenu_id
GO

-- ===== Bài 1.5 — Chứng từ được lưu ở bảng nào =====
USE FBOSP2422_App
GO
SELECT ma_ct, [c$] AS bang_c, [m$] AS bang_master, [d$] AS bang_detail, [i$] AS bang_i, url
FROM dmct9
WHERE ma_ct IN ('PND', 'HDA')
GO

-- ===== Bài 1.6 — Bảng chia theo kỳ =====
USE FBOSP2422_App
GO
SELECT name
FROM sys.tables
WHERE name LIKE 'm74$2026%'
ORDER BY name
GO

-- ===== Bài 1.7 — Trạng thái của chứng từ =====
USE FBOSP2422_App
GO
SELECT ma_ct, status, statusname, statusname2, xdefault
FROM dmttct
WHERE ma_ct IN ('PND', 'HDA')
ORDER BY ma_ct, status
GO

-- ===== Bài 1.8 — Tham số hệ thống =====
USE FBOSP2422_App
GO
SELECT name, val
FROM options
WHERE name IN ('m_ma_nt0', 'm_delete_log')
GO

-- ===== Bài 1.10 — Một hoá đơn được ghi vào những sổ nào? =====
USE FBOSP2422_App
GO
DECLARE @r char(13)
SELECT @r = stt_rec FROM m81$202601 WHERE so_ct = 'HD0001'
SELECT N'm81 (master)' AS bang, COUNT(*) AS so_dong FROM m81$202601 WHERE stt_rec = @r
UNION ALL SELECT N'd81 (chi tiết)', COUNT(*) FROM d81$202601 WHERE stt_rec = @r
UNION ALL SELECT N'c81 (sổ chứng từ)', COUNT(*) FROM c81$000000 WHERE stt_rec = @r
UNION ALL SELECT N'i81 (tra cứu)', COUNT(*) FROM i81$202601 WHERE stt_rec = @r
UNION ALL SELECT N'r00 (sổ cái)', COUNT(*) FROM r00$202601 WHERE stt_rec = @r
UNION ALL SELECT N'r70 (sổ kho)', COUNT(*) FROM r70$202601 WHERE stt_rec = @r
UNION ALL SELECT N'r90 (sổ kho thực tế)', COUNT(*) FROM r90$202601 WHERE stt_rec = @r
UNION ALL SELECT N'r20 (sổ thuế)', COUNT(*) FROM r20$202601 WHERE stt_rec = @r
UNION ALL SELECT N'ctgt21 (người mua)', COUNT(*) FROM ctgt21 WHERE stt_rec = @r
UNION ALL SELECT N'hddt00 (HĐĐT)', COUNT(*) FROM hddt00$202601 WHERE stt_rec = @r

-- Bút toán sổ cái của hoá đơn
SELECT tk, tk_du, ps_no, ps_co, ma_kh, dien_giai FROM r00$202601 WHERE stt_rec = @r ORDER BY line_nbr
GO


/* ######################## BUỔI 2 — SQL CĂN BẢN ĐỂ ĐỌC DỮ LIỆU FAST ######################## */

-- ===== Bài 2.1 — Khách hàng còn sử dụng =====
USE FBOSP2422_App
GO
SELECT ma_kh, ten_kh, ma_so_thue, ma_nvbh
FROM dmkh
WHERE kh_yn = 1 AND status = '1'
ORDER BY ma_kh
GO

-- ===== Bài 2.2 — Khách hàng kèm tên nhân viên bán hàng =====
USE FBOSP2422_App
GO
SELECT a.ma_kh, a.ten_kh, a.ma_nvbh, b.ten_nvbh
FROM dmkh a
LEFT JOIN dmnvbh b ON b.ma_nvbh = a.ma_nvbh
WHERE a.kh_yn = 1
ORDER BY a.ma_kh
GO

-- ===== Bài 2.3 — Số khách hàng của từng nhân viên bán hàng =====
USE FBOSP2422_App
GO
SELECT n.ma_nvbh, n.ten_nvbh, COUNT(k.ma_kh) AS so_khach
FROM dmnvbh n
LEFT JOIN dmkh k ON k.ma_nvbh = n.ma_nvbh AND k.kh_yn = 1
GROUP BY n.ma_nvbh, n.ten_nvbh
ORDER BY n.ma_nvbh
GO

-- ===== Bài 2.4 — Tên khách hàng bị trùng =====
USE FBOSP2422_App
GO
SELECT ten_kh, COUNT(*) AS so_lan, STRING_AGG(ma_kh, ', ') AS cac_ma
FROM dmkh
GROUP BY ten_kh
HAVING COUNT(*) > 1
GO

-- ===== Bài 2.5 — Doanh thu tháng 01/2026 theo khách hàng =====
USE FBOSP2422_App
GO
SELECT a.ma_kh, k.ten_kh, COUNT(*) AS so_hd,
       SUM(a.t_tien2) AS tien_hang, SUM(a.t_thue) AS thue, SUM(a.t_tt) AS thanh_toan
FROM m81$202601 a
JOIN dmkh k ON k.ma_kh = a.ma_kh
WHERE a.status NOT IN ('9', '*')
GROUP BY a.ma_kh, k.ten_kh
ORDER BY a.ma_kh
GO

-- ===== Bài 2.6 — Chi tiết phiếu nhập tháng 02/2026 =====
USE FBOSP2422_App
GO
SELECT m.so_ct, m.ngay_ct, m.status, d.line_nbr, d.ma_vt, v.ten_vt, d.ma_kho, k.ten_kho,
       d.so_luong, d.gia, d.tien
FROM m74$202602 m
JOIN d74$202602 d ON d.stt_rec = m.stt_rec
LEFT JOIN dmvt v  ON v.ma_vt = d.ma_vt
LEFT JOIN dmkho k ON k.ma_kho = d.ma_kho
WHERE m.status <> '*'
ORDER BY m.so_ct, d.line_nbr
GO

-- ===== Bài 2.7 — Tổng nhập theo vật tư cả quý 1 =====
USE FBOSP2422_App
GO
SELECT x.ma_vt, v.ten_vt, SUM(x.so_luong) AS so_luong, SUM(x.tien) AS tien
FROM (
    SELECT d.ma_vt, d.so_luong, d.tien FROM m74$202601 m JOIN d74$202601 d ON d.stt_rec = m.stt_rec WHERE m.status IN ('1','2','3')
    UNION ALL
    SELECT d.ma_vt, d.so_luong, d.tien FROM m74$202602 m JOIN d74$202602 d ON d.stt_rec = m.stt_rec WHERE m.status IN ('1','2','3')
    UNION ALL
    SELECT d.ma_vt, d.so_luong, d.tien FROM m74$202603 m JOIN d74$202603 d ON d.stt_rec = m.stt_rec WHERE m.status IN ('1','2','3')
) x
JOIN dmvt v ON v.ma_vt = x.ma_vt
GROUP BY x.ma_vt, v.ten_vt
ORDER BY x.ma_vt
GO

-- ===== Bài 2.8 — Khách hàng không mua hàng trong quý =====
USE FBOSP2422_App
GO
SELECT k.ma_kh, k.ten_kh, k.status
FROM dmkh k
WHERE k.kh_yn = 1
  AND NOT EXISTS (
        SELECT 1 FROM (
            SELECT ma_kh, status FROM m81$202601 UNION ALL
            SELECT ma_kh, status FROM m81$202602 UNION ALL
            SELECT ma_kh, status FROM m81$202603) h
        WHERE h.ma_kh = k.ma_kh AND h.status NOT IN ('9', '*'))
ORDER BY k.ma_kh
GO

-- ===== Bài 2.9 — Vật tư chưa từng nhập kho =====
USE FBOSP2422_App
GO
SELECT v.ma_vt, v.ten_vt
FROM dmvt v
WHERE NOT EXISTS (
    SELECT 1 FROM (
        SELECT ma_vt FROM d74$202601 UNION ALL SELECT ma_vt FROM d74$202602 UNION ALL SELECT ma_vt FROM d74$202603) d
    WHERE d.ma_vt = v.ma_vt)
GO

-- ===== Bài 2.10 — Hàm không chạy trên database Fast =====
USE FBOSP2422_App
GO
SELECT name, compatibility_level FROM sys.databases WHERE name = DB_NAME()

-- SELECT TRY_CONVERT(int, 'abc')   --> lỗi: 'TRY_CONVERT' is not a recognized built-in function name
SELECT v.s, CASE WHEN v.s NOT LIKE '%[^0-9]%' AND v.s <> '' THEN CAST(v.s AS int) END AS so
FROM (SELECT '123' AS s UNION ALL SELECT 'abc' UNION ALL SELECT '') v
GO


/* ######################## BUỔI 3 — SỬA DỮ LIỆU AN TOÀN VÀ CÔNG CỤ DỮ LIỆU BCODE ######################## */

-- ===== Bài 3.1 — Sửa dữ liệu đúng quy trình 5 bước =====
USE FBOSP2422_App
GO
-- 1) Đếm số khách hàng sẽ sửa
SELECT COUNT(*) AS so_khach_nv01 FROM dmkh WHERE ma_nvbh = 'NV01'

-- 2) Sao lưu (xoá bảng sao lưu cũ nếu có)
IF OBJECT_ID('zbk_dmkh_bt31') IS NOT NULL DROP TABLE zbk_dmkh_bt31
SELECT * INTO zbk_dmkh_bt31 FROM dmkh WHERE ma_nvbh = 'NV01'

-- 3) + 4) Sửa trong transaction rồi kiểm tra số dòng đã sửa
BEGIN TRAN
UPDATE dmkh SET ma_nvbh = 'NV03', datetime2 = GETDATE() WHERE ma_nvbh = 'NV01'
SELECT @@ROWCOUNT AS so_dong_da_sua
SELECT ma_kh, ma_nvbh FROM dmkh WHERE ma_kh IN (SELECT ma_kh FROM zbk_dmkh_bt31)

-- 5) Vì là bài tập nên luôn huỷ thay đổi
ROLLBACK
SELECT ma_nvbh, COUNT(*) AS so_khach FROM dmkh WHERE ma_nvbh IN ('NV01', 'NV03') GROUP BY ma_nvbh
GO

-- ===== Bài 3.2 — Khôi phục từ bảng sao lưu =====
-- (cần làm bài 3.1 trước)
USE FBOSP2422_App
GO
BEGIN TRAN
UPDATE dmkh SET ma_nvbh = 'NV03' WHERE ma_nvbh = 'NV01'      -- giả lập tình huống đã lỡ sửa

UPDATE a SET a.ma_nvbh = b.ma_nvbh, a.datetime2 = GETDATE()   -- khôi phục từ bảng sao lưu
FROM dmkh a JOIN zbk_dmkh_bt31 b ON b.ma_kh = a.ma_kh
SELECT @@ROWCOUNT AS so_dong_khoi_phuc
SELECT ma_nvbh, COUNT(*) AS so_khach FROM dmkh WHERE ma_nvbh IN ('NV01', 'NV03') GROUP BY ma_nvbh
ROLLBACK

DROP TABLE zbk_dmkh_bt31
GO

-- ===== Bài 3.3 — UPDATE quên WHERE =====
USE FBOSP2422_App
GO
BEGIN TRAN
UPDATE dmvt SET status = '0'            -- thiếu WHERE: sửa toàn bộ bảng!
SELECT @@ROWCOUNT AS so_dong_bi_sua, (SELECT COUNT(*) FROM dmvt) AS tong_vat_tu
ROLLBACK
SELECT status, COUNT(*) AS so_vt FROM dmvt GROUP BY status
GO

-- ===== Bài 3.4 — Hoá đơn lớn nhất của mỗi khách hàng (CTE + ROW_NUMBER) =====
USE FBOSP2422_App
GO
;WITH hd AS (
    SELECT ma_kh, so_ct, ngay_ct, t_tt, status FROM m81$202601 UNION ALL
    SELECT ma_kh, so_ct, ngay_ct, t_tt, status FROM m81$202602 UNION ALL
    SELECT ma_kh, so_ct, ngay_ct, t_tt, status FROM m81$202603
), xh AS (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY ma_kh ORDER BY t_tt DESC) AS rn
    FROM hd WHERE status NOT IN ('9', '*')
)
SELECT ma_kh, so_ct, ngay_ct, t_tt FROM xh WHERE rn = 1 ORDER BY ma_kh
GO

-- ===== Bài 3.5 — Bảng tạm: doanh thu của nhân viên bán hàng theo tháng =====
USE FBOSP2422_App
GO
IF OBJECT_ID('tempdb..#dt') IS NOT NULL DROP TABLE #dt
SELECT ma_nvbh, MONTH(ngay_ct) AS thang, t_tien2 INTO #dt FROM m81$202601 WHERE status NOT IN ('9','*')
INSERT INTO #dt SELECT ma_nvbh, MONTH(ngay_ct), t_tien2 FROM m81$202602 WHERE status NOT IN ('9','*')
INSERT INTO #dt SELECT ma_nvbh, MONTH(ngay_ct), t_tien2 FROM m81$202603 WHERE status NOT IN ('9','*')

SELECT CASE WHEN ISNULL(ma_nvbh, '') = '' THEN N'(chưa gán)' ELSE ma_nvbh END AS nvbh,
       SUM(CASE WHEN thang = 1 THEN t_tien2 ELSE 0 END) AS thang_1,
       SUM(CASE WHEN thang = 2 THEN t_tien2 ELSE 0 END) AS thang_2,
       SUM(CASE WHEN thang = 3 THEN t_tien2 ELSE 0 END) AS thang_3,
       SUM(t_tien2) AS tong
FROM #dt GROUP BY CASE WHEN ISNULL(ma_nvbh, '') = '' THEN N'(chưa gán)' ELSE ma_nvbh END ORDER BY 1
DROP TABLE #dt
GO

-- ===== Bài 3.6 — Kiểm tra cấu trúc các bảng kỳ =====
USE FBOSP2422_App
GO
SELECT TABLE_NAME, COUNT(*) AS so_cot,
       (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'm74$000000') AS so_cot_mau
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME LIKE 'm74$2026%'
GROUP BY TABLE_NAME
ORDER BY TABLE_NAME
GO

-- ===== Bài 3.7 — Ai đang dùng bảng này? =====
USE FBOSP2422_App
GO
SELECT COUNT(*) AS so_object
FROM sys.sql_modules m WHERE m.definition LIKE '%dmttct%'

SELECT TOP 10 o.name, o.type_desc
FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
WHERE m.definition LIKE '%dmttct%'
ORDER BY o.name
GO


/* ######################## BUỔI 4 — ĐỌC VÀ SỬA SOURCE BẰNG BCODE ######################## */

-- ===== Bài 4.2 — Đọc field tra cứu rồi viết SQL tương ứng =====
USE FBOSP2422_App
GO
SELECT k.ma_kh, k.ten_kh, k.tk, t.ten_tk
FROM dmkh k
LEFT JOIN dmtk t ON t.tk = k.tk
WHERE k.ma_kh IN ('KH001', 'NCC01')
GO

-- ===== Bài 4.3 — Từ bảng tìm ra menu =====
USE FBOSP2422_Sys
GO
SELECT wmenu_id, bar, link, sysid
FROM wcommand
WHERE sysid = 'Customer'
ORDER BY wmenu_id
GO


/* ######################## BUỔI 5 — THÊM DANH MỤC ######################## */

-- ===== Bài 5.1 — Kiểu dữ liệu của cột tra cứu =====
USE FBOSP2422_App
GO
SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE (TABLE_NAME = 'dmkh' AND COLUMN_NAME = 'ma_kh') OR (TABLE_NAME = 'dmvt' AND COLUMN_NAME = 'ma_vt')
   OR (TABLE_NAME = 'dmkho' AND COLUMN_NAME = 'ma_kho') OR (TABLE_NAME = 'dmnvbh' AND COLUMN_NAME = 'ma_nvbh')
GO

-- ===== Bài 5.2 — Tạo bảng danh mục =====
USE FBOSP2422_App
GO
IF OBJECT_ID('zdmhtbh') IS NULL
CREATE TABLE zdmhtbh (
    ma_htbh   VARCHAR(16)   NOT NULL,
    ten_htbh  NVARCHAR(128) NOT NULL,
    ten_htbh2 NVARCHAR(128) NULL,
    ma_kh     VARCHAR(16)   NULL,          -- cùng kiểu với dmkh.ma_kh varchar(16)
    ghi_chu   NVARCHAR(512) NULL,
    status    CHAR(1)       NOT NULL DEFAULT '1',
    datetime0 DATETIME NULL, datetime2 DATETIME NULL,
    user_id0  INT NULL,      user_id2  INT NULL,
    CONSTRAINT PK_zdmhtbh PRIMARY KEY (ma_htbh))

DELETE FROM zdmhtbh WHERE ma_htbh IN ('LE', 'SI', 'ONL')
INSERT INTO zdmhtbh (ma_htbh, ten_htbh, ten_htbh2, ma_kh, status, datetime0, datetime2, user_id0, user_id2) VALUES
 ('LE',  N'Bán lẻ',       N'Retail',    'KH004', '1', GETDATE(), GETDATE(), -1, -1),
 ('SI',  N'Bán sỉ',       N'Wholesale', 'KH001', '1', GETDATE(), GETDATE(), -1, -1),
 ('ONL', N'Bán online',   N'Online',    'KH099', '1', GETDATE(), GETDATE(), -1, -1)

-- Dòng có ma_kh không tồn tại trong dmkh
SELECT h.ma_htbh, h.ma_kh
FROM zdmhtbh h
WHERE h.ma_kh IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dmkh k WHERE k.ma_kh = h.ma_kh)
GO


/* ######################## BUỔI 6 — THÊM BÁO CÁO ######################## */

-- ===== Bài 6.1 — Procedure báo cáo theo khung chuẩn của Fast =====
USE FBOSP2422_App
GO
IF OBJECT_ID('zbt_rs_BangKeNhapVatTu') IS NOT NULL DROP PROCEDURE zbt_rs_BangKeNhapVatTu
GO
CREATE PROCEDURE zbt_rs_BangKeNhapVatTu
    @DateFrom SMALLDATETIME,
    @DateTo   SMALLDATETIME,
    @Kho      VARCHAR(16),        -- truyền '' để lấy mọi kho
    @Unit     VARCHAR(1023),
    @Language CHAR(1),
    @UserID   INT,
    @Admin    BIT
AS
BEGIN
    SET NOCOUNT ON
    DECLARE @UnitKey NVARCHAR(1023), @Key NVARCHAR(4000), @q NVARCHAR(4000)
    SET @UnitKey = dbo.FastBusiness$Function$System$GetUnitFilter('a.ma_dvcs', @Unit, @UserID, @Admin)

    -- Tạo bảng tạm có kiểu cột giống hệt bảng chi tiết
    SELECT TOP 0 b.ma_vt, b.so_luong, b.tien INTO #ct FROM d74$000000 b

    -- Ghép các điều kiện lọc thành một chuỗi
    SET @Key = 'a.status in (''1'', ''2'', ''3'')'
    IF @UnitKey IS NOT NULL SET @Key = @Key + ' and ' + @UnitKey
    IF @Kho <> '' SET @Key = @Key + ' and b.ma_kho = ''' + REPLACE(RTRIM(@Kho), '''', '''''') + ''''
    SET @Key = dbo.FastBusiness$Function$System$GetCheckKey(@Key)

    -- Chạy trên mọi bảng kỳ trong khoảng ngày
    SET @q = 'insert into #ct select b.ma_vt, sum(b.so_luong), sum(b.tien)'
           + ' from m74$%Partition a with(nolock) join d74$%Partition b with(nolock) on b.stt_rec = a.stt_rec'
           + ' where %[' + @Key + ']% group by b.ma_vt'
    EXEC FastBusiness$Partition$Execute @q, NULL, 'a.ngay_ct', @DateFrom, @DateTo, @UserID, @Admin

    SELECT c.ma_vt, CASE WHEN @Language = 'e' THEN v.ten_vt2 ELSE v.ten_vt END AS ten_vt,
           SUM(c.so_luong) AS so_luong, SUM(c.tien) AS tien
    FROM #ct c JOIN dmvt v ON v.ma_vt = c.ma_vt
    GROUP BY c.ma_vt, v.ten_vt, v.ten_vt2
    ORDER BY c.ma_vt
END
GO
EXEC zbt_rs_BangKeNhapVatTu '20260101', '20260331', '', 'CTY', 'v', 1, 1
GO

-- ===== Bài 6.2 — Lọc theo kho, hiển thị tiếng Anh =====
-- (cần làm bài 6.1 trước)
USE FBOSP2422_App
GO
EXEC zbt_rs_BangKeNhapVatTu '20260101', '20260331', 'KHO2', 'CTY', 'e', 1, 1
GO

-- ===== Bài 6.3 — Khoảng ngày từ giữa tháng này đến giữa tháng sau =====
-- (cần làm bài 6.1 trước)
USE FBOSP2422_App
GO
EXEC zbt_rs_BangKeNhapVatTu '20260110', '20260210', '', 'CTY', 'v', 1, 1
GO

-- ===== Bài 6.4 — Nhập – xuất – tồn từ sổ kho =====
USE FBOSP2422_App
GO
;WITH k AS (
    SELECT ma_vt, ma_kho, sl_nhap, sl_xuat, tien_nhap, tien_xuat, stt_rec FROM r70$202601 UNION ALL
    SELECT ma_vt, ma_kho, sl_nhap, sl_xuat, tien_nhap, tien_xuat, stt_rec FROM r70$202602 UNION ALL
    SELECT ma_vt, ma_kho, sl_nhap, sl_xuat, tien_nhap, tien_xuat, stt_rec FROM r70$202603
)
SELECT k.ma_vt, k.ma_kho, SUM(k.sl_nhap) AS sl_nhap, SUM(k.sl_xuat) AS sl_xuat,
       SUM(k.sl_nhap - k.sl_xuat) AS sl_ton, SUM(k.tien_nhap - k.tien_xuat) AS gia_tri_ton,
       MAX(c.ton13) AS ton_hien_thoi
FROM k
LEFT JOIN cdvt13 c ON c.ma_vt = k.ma_vt AND c.ma_kho = k.ma_kho
WHERE k.stt_rec LIKE 'BT%'
GROUP BY k.ma_vt, k.ma_kho
ORDER BY k.ma_vt, k.ma_kho
GO


/* ######################## BUỔI 7 — VẼ MẪU IN ######################## */

-- ===== Bài 7.1 — Dữ liệu đầu phiếu (bảng d1) =====
USE FBOSP2422_App
GO
SELECT ltrim(a.so_ct) AS so_ct,
       replace(str(day(a.ngay_ct), 2), ' ', '0') AS ngay, replace(str(month(a.ngay_ct), 2), ' ', '0') AS thang,
       cast(year(a.ngay_ct) AS char(4)) AS nam,
       d.ten_kh AS ong_ba, a.dien_giai, a.t_tien,
       dbo.FastBusiness$Function$System$ReadCurrency('VND', a.t_tien, 'V') AS t_tien_in_word,
       dbo.FastBusiness$Function$System$ReadCurrency('VND', a.t_tien, 'E') AS t_tien_in_word_e
FROM m74$202601 a
LEFT JOIN dmkh d ON d.ma_kh = a.ma_kh
WHERE a.so_ct = 'PN0001'
GO

-- ===== Bài 7.2 — Dữ liệu chi tiết (bảng d2) lấy bằng procedure in chuẩn =====
USE FBOSP2422_App
GO
DECLARE @stt_rec char(13), @key nvarchar(200)
SELECT @stt_rec = stt_rec FROM m74$202601 WHERE so_ct = 'PN0001'
SET @key = 'a.stt_rec = ''' + @stt_rec + ''''
EXEC rs_PrintIRTran 'V', @key, 'd74$202601', 'PND', 0        -- không truyền 2 tham số cuối để dùng giá trị mặc định
GO


/* ######################## BUỔI 8 — CHỈNH SỬA PROCEDURE ######################## */

-- ===== Bài 8.1 — Đọc procedure trước khi sửa =====
USE FBOSP2422_App
GO
SELECT p.parameter_id, p.name, TYPE_NAME(p.user_type_id) AS kieu, p.max_length
FROM sys.parameters p WHERE p.object_id = OBJECT_ID('dbo.rs_PrintIRTran') ORDER BY p.parameter_id

SELECT name, create_date, modify_date FROM sys.procedures WHERE name = 'rs_PrintIRTran'

SELECT COUNT(*) AS so_object_goi
FROM sys.dm_sql_referencing_entities('dbo.rs_PrintIRTran', 'OBJECT')
GO

-- ===== Bài 8.2 — Dùng ALTER để thêm cột mà vẫn giữ quyền =====
-- (cần làm bài 6.1 trước)
USE FBOSP2422_App
GO
ALTER PROCEDURE zbt_rs_BangKeNhapVatTu
    @DateFrom SMALLDATETIME,
    @DateTo   SMALLDATETIME,
    @Kho      VARCHAR(16),        -- truyền '' để lấy mọi kho
    @Unit     VARCHAR(1023),
    @Language CHAR(1),
    @UserID   INT,
    @Admin    BIT
AS
BEGIN
    SET NOCOUNT ON
    DECLARE @UnitKey NVARCHAR(1023), @Key NVARCHAR(4000), @q NVARCHAR(4000)
    SET @UnitKey = dbo.FastBusiness$Function$System$GetUnitFilter('a.ma_dvcs', @Unit, @UserID, @Admin)

    -- Tạo bảng tạm có kiểu cột giống hệt bảng chi tiết
    SELECT TOP 0 b.ma_vt, b.so_luong, b.tien INTO #ct FROM d74$000000 b

    -- Ghép các điều kiện lọc thành một chuỗi
    SET @Key = 'a.status in (''1'', ''2'', ''3'')'
    IF @UnitKey IS NOT NULL SET @Key = @Key + ' and ' + @UnitKey
    IF @Kho <> '' SET @Key = @Key + ' and b.ma_kho = ''' + REPLACE(RTRIM(@Kho), '''', '''''') + ''''
    SET @Key = dbo.FastBusiness$Function$System$GetCheckKey(@Key)

    -- Chạy trên mọi bảng kỳ trong khoảng ngày
    SET @q = 'insert into #ct select b.ma_vt, sum(b.so_luong), sum(b.tien)'
           + ' from m74$%Partition a with(nolock) join d74$%Partition b with(nolock) on b.stt_rec = a.stt_rec'
           + ' where %[' + @Key + ']% group by b.ma_vt'
    EXEC FastBusiness$Partition$Execute @q, NULL, 'a.ngay_ct', @DateFrom, @DateTo, @UserID, @Admin

    SELECT c.ma_vt, CASE WHEN @Language = 'e' THEN v.ten_vt2 ELSE v.ten_vt END AS ten_vt, v.dvt,
           SUM(c.so_luong) AS so_luong, SUM(c.tien) AS tien,
           SUM(c.tien) / NULLIF(SUM(c.so_luong), 0) AS gia_bq
    FROM #ct c JOIN dmvt v ON v.ma_vt = c.ma_vt
    GROUP BY c.ma_vt, v.ten_vt, v.ten_vt2, v.dvt
    ORDER BY c.ma_vt
END
GO
SELECT COUNT(*) AS so_tham_so FROM sys.parameters WHERE object_id = OBJECT_ID('dbo.zbt_rs_BangKeNhapVatTu')
SELECT name, create_date, modify_date FROM sys.procedures WHERE name = 'zbt_rs_BangKeNhapVatTu'
EXEC zbt_rs_BangKeNhapVatTu '20260101', '20260331', '', 'CTY', 'v', 1, 1
GO

-- ===== Bài 8.3 — Lỗi chia cho 0 =====
USE FBOSP2422_App
GO
-- Cách sai: SUM(tien2) / SUM(so_luong) -> lỗi Divide by zero (hoặc ra NULL) với vật tư chưa bán
SELECT v.ma_vt, v.ten_vt, ISNULL(SUM(d.so_luong), 0) AS sl_ban,
       ISNULL(SUM(d.tien2) / NULLIF(SUM(d.so_luong), 0), 0) AS gia_ban_bq
FROM dmvt v
LEFT JOIN (
    SELECT d.ma_vt, d.so_luong, d.tien2 FROM d81$202601 d JOIN m81$202601 m ON m.stt_rec = d.stt_rec WHERE m.status NOT IN ('9','*') UNION ALL
    SELECT d.ma_vt, d.so_luong, d.tien2 FROM d81$202602 d JOIN m81$202602 m ON m.stt_rec = d.stt_rec WHERE m.status NOT IN ('9','*') UNION ALL
    SELECT d.ma_vt, d.so_luong, d.tien2 FROM d81$202603 d JOIN m81$202603 m ON m.stt_rec = d.stt_rec WHERE m.status NOT IN ('9','*')
) d ON d.ma_vt = v.ma_vt
GROUP BY v.ma_vt, v.ten_vt
ORDER BY v.ma_vt
GO

-- ===== Bài 8.4 — Xung đột collation =====
USE FBOSP2422_App
GO
IF OBJECT_ID('tempdb..#ds') IS NOT NULL DROP TABLE #ds
CREATE TABLE #ds (ma_kh varchar(16) COLLATE Vietnamese_CI_AS)
INSERT INTO #ds VALUES ('KH001'), ('KH003')

-- SELECT k.ma_kh, k.ten_kh FROM dmkh k JOIN #ds d ON d.ma_kh = k.ma_kh
--   --> Cannot resolve the collation conflict between "Vietnamese_CI_AS" and "SQL_Latin1_General_CP1_CI_AS"

-- Cách 1: ép collation trong điều kiện JOIN
SELECT k.ma_kh, k.ten_kh FROM dmkh k JOIN #ds d ON d.ma_kh COLLATE DATABASE_DEFAULT = k.ma_kh

-- Cách 2: tạo bảng tạm theo đúng cột của dmkh
IF OBJECT_ID('tempdb..#ds2') IS NOT NULL DROP TABLE #ds2
SELECT TOP 0 ma_kh INTO #ds2 FROM dmkh
INSERT INTO #ds2 VALUES ('KH001'), ('KH003')
SELECT k.ma_kh, k.ten_kh FROM dmkh k JOIN #ds2 d ON d.ma_kh = k.ma_kh
DROP TABLE #ds
DROP TABLE #ds2
GO

-- ===== Bài 8.5 — Macro FCode không chạy được ngoài Fast =====
USE FBOSP2422_App
GO
DECLARE @ma_kh varchar(16), @userID int, @language char(1)
SELECT @ma_kh = 'KH001', @userID = 1, @language = 'v'

IF EXISTS (SELECT * FROM dmkh WHERE ma_kh = @ma_kh)
    SELECT 'ma_kh' AS field,
           CASE WHEN @language = 'v' THEN N'Dữ liệu này đã cập nhật trong danh mục. Xin kiểm tra lại'
                ELSE N'The record is invalid or already exists.' END AS message
GO


/* ######################## BUỔI 9 — BẮT LỖI VÀ XỬ LÝ SỰ CỐ THỰC TẾ ######################## */

-- ===== Bài 9.1 — “Lọc NV02 vẫn ra hoá đơn của NV01” =====
USE FBOSP2422_App
GO
-- Chạy procedure để thấy lỗi
EXEC zbt_loi_DoanhThuNVBH '20260101', '20260331', 'NV02'
GO
-- Cách sửa: bọc ngoặc đoạn điều kiện có OR; tốt hơn nữa là dùng NOT IN như bản sửa dưới đây
ALTER PROCEDURE zbt_loi_DoanhThuNVBH @DateFrom smalldatetime, @DateTo smalldatetime, @Nvbh varchar(8)
AS
BEGIN
    SET NOCOUNT ON
    DECLARE @Key nvarchar(4000), @q nvarchar(4000)
    SELECT TOP 0 so_ct, ngay_ct, ma_kh, ma_nvbh, status, t_tien2 INTO #hd FROM m81$000000
    SET @Key = 'a.status not in (''9'', ''*'')'
    IF @Nvbh <> '' SET @Key = @Key + ' and a.ma_nvbh = ''' + REPLACE(@Nvbh, '''', '''''') + ''''
    SET @q = 'insert into #hd select so_ct, ngay_ct, ma_kh, ma_nvbh, status, t_tien2 from m81$%Partition a where %[' + @Key + ']%'
    EXEC FastBusiness$Partition$Execute @q, NULL, 'a.ngay_ct', @DateFrom, @DateTo, 1, 1
    SELECT * FROM #hd ORDER BY so_ct
END
GO
EXEC zbt_loi_DoanhThuNVBH '20260101', '20260331', 'NV02'
GO

-- ===== Bài 9.2 — “Tổng nhập theo nhà cung cấp mỗi lần chạy ra một số khác” =====
USE FBOSP2422_App
GO
EXEC zbt_loi_TongNhapNCC
GO
-- Cách sửa: cộng tổng theo nhà cung cấp trước, rồi mới gán vào bảng tạm
ALTER PROCEDURE zbt_loi_TongNhapNCC
AS
BEGIN
    SET NOCOUNT ON
    SELECT ma_kh, ten_kh, CAST(0 AS numeric(19,4)) AS tong_nhap INTO #ncc FROM dmkh WHERE cc_yn = 1
    UPDATE n SET n.tong_nhap = x.tien
    FROM #ncc n
    JOIN (SELECT m.ma_kh, SUM(d.tien) AS tien
          FROM m74$202601 m JOIN d74$202601 d ON d.stt_rec = m.stt_rec
          WHERE m.status IN ('1','2','3') GROUP BY m.ma_kh) x ON x.ma_kh = n.ma_kh
    SELECT * FROM #ncc ORDER BY ma_kh
END
GO
EXEC zbt_loi_TongNhapNCC

-- Đối chiếu: tự tính tổng nhập theo nhà cung cấp
SELECT m.ma_kh, SUM(d.tien) AS tong_nhap
FROM m74$202601 m JOIN d74$202601 d ON d.stt_rec = m.stt_rec
WHERE m.status IN ('1','2','3') GROUP BY m.ma_kh
GO

-- ===== Bài 9.3 — “Báo cáo quý chỉ ra số liệu của tháng 1” =====
USE FBOSP2422_App
GO
EXEC zbt_loi_DoanhThuQuy '20260101', '20260331'
GO
ALTER PROCEDURE zbt_loi_DoanhThuQuy @DateFrom smalldatetime, @DateTo smalldatetime
AS
BEGIN
    SET NOCOUNT ON
    DECLARE @q nvarchar(4000)
    SELECT TOP 0 ma_kh, t_tien2 INTO #hd FROM m81$000000
    SET @q = 'insert into #hd select ma_kh, t_tien2 from m81$%Partition a where %[a.status not in (''9'', ''*'')]%'
    EXEC FastBusiness$Partition$Execute @q, NULL, 'a.ngay_ct', @DateFrom, @DateTo, 1, 1
    SELECT COUNT(*) AS so_hd, SUM(t_tien2) AS doanh_thu FROM #hd
END
GO
EXEC zbt_loi_DoanhThuQuy '20260101', '20260331'
GO

-- ===== Bài 9.4 — “Divide by zero encountered” =====
USE FBOSP2422_App
GO
BEGIN TRY
    EXEC zbt_loi_GiaBanBQ
END TRY
BEGIN CATCH
    SELECT ERROR_MESSAGE() AS loi
END CATCH
GO
ALTER PROCEDURE zbt_loi_GiaBanBQ
AS
BEGIN
    SET NOCOUNT ON
    SELECT v.ma_vt, ISNULL(SUM(d.tien2), 0) AS tien, ISNULL(SUM(d.so_luong), 0) AS sl,
           ISNULL(SUM(d.tien2) / NULLIF(SUM(d.so_luong), 0), 0) AS gia_bq
    FROM dmvt v LEFT JOIN d81$202601 d ON d.ma_vt = v.ma_vt
    GROUP BY v.ma_vt ORDER BY v.ma_vt
END
GO
EXEC zbt_loi_GiaBanBQ
GO

-- ===== Bài 9.5 — “Invalid column name” khi chạy script triển khai =====
USE FBOSP2422_App
GO
-- Cách sai (hai lệnh nằm trong cùng một batch):
-- ALTER TABLE zbt_loi_Bang ADD zghi_chu nvarchar(100)
-- UPDATE zbt_loi_Bang SET zghi_chu = N'đã kiểm'

-- Cách đúng: tách thành hai batch bằng GO
IF COL_LENGTH('zbt_loi_Bang', 'zghi_chu') IS NULL
    ALTER TABLE zbt_loi_Bang ADD zghi_chu nvarchar(100)
GO
UPDATE zbt_loi_Bang SET zghi_chu = N'đã kiểm'
SELECT * FROM zbt_loi_Bang
GO

-- ===== Bài 9.6 — “Sửa rồi mà vẫn lỗi” — đang chạy đúng bản đã sửa chưa? =====
USE FBOSP2422_App
GO
SELECT p.name, p.modify_date,
       (SELECT COUNT(*) FROM sys.parameters x WHERE x.object_id = p.object_id) AS so_tham_so
FROM sys.procedures p WHERE p.name LIKE 'zbt[_]loi[_]%' ORDER BY p.name

SELECT TOP 5 OBJECT_NAME(object_id, database_id) AS proc_name, last_execution_time, execution_count
FROM sys.dm_exec_procedure_stats WHERE database_id = DB_ID() AND OBJECT_NAME(object_id, database_id) LIKE 'zbt%'
ORDER BY last_execution_time DESC
GO

-- ===== Bài 9.7 — Đối chiếu sổ cái với hoá đơn =====
USE FBOSP2422_App
GO
;WITH gl AS (
    SELECT stt_rec, tk, ps_no, ps_co, ma_kh FROM r00$202601 UNION ALL
    SELECT stt_rec, tk, ps_no, ps_co, ma_kh FROM r00$202602 UNION ALL
    SELECT stt_rec, tk, ps_no, ps_co, ma_kh FROM r00$202603
), hd AS (
    SELECT stt_rec, so_ct, ma_kh, t_tt, status FROM m81$202601 UNION ALL
    SELECT stt_rec, so_ct, ma_kh, t_tt, status FROM m81$202602 UNION ALL
    SELECT stt_rec, so_ct, ma_kh, t_tt, status FROM m81$202603
)
SELECT
  (SELECT SUM(ps_no) FROM gl) AS tong_no, (SELECT SUM(ps_co) FROM gl) AS tong_co,
  (SELECT SUM(ps_no) FROM gl WHERE tk LIKE '131%') AS no_131,
  (SELECT SUM(t_tt) FROM hd WHERE status = '2') AS tt_hoa_don

;WITH gl AS (
    SELECT stt_rec FROM r00$202601 UNION ALL SELECT stt_rec FROM r00$202602 UNION ALL SELECT stt_rec FROM r00$202603
), hd AS (
    SELECT stt_rec, so_ct, status FROM m81$202601 UNION ALL SELECT stt_rec, so_ct, status FROM m81$202602 UNION ALL SELECT stt_rec, so_ct, status FROM m81$202603
)
SELECT h.so_ct, h.status FROM hd h
WHERE h.status = '2' AND NOT EXISTS (SELECT 1 FROM gl g WHERE g.stt_rec = h.stt_rec)
GO


/* ######################## BUỔI 10 — ĐÓNG GÓI, TRIỂN KHAI VÀ ÔN TẬP ######################## */

-- ===== Bài 10.1 — Liệt kê các object đã tạo khi tập =====
USE FBOSP2422_App
GO
SELECT name, type_desc, create_date, modify_date
FROM sys.objects
WHERE (name LIKE 'zbt%' OR name LIKE 'zdm%') AND type IN ('U', 'P', 'FN', 'TF', 'V')
ORDER BY type_desc, name
GO
