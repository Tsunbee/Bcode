/* =====================================================================================
   BÀI TẬP ĐÀO TẠO — 09. DỰNG TÌNH HUỐNG LỖI (Buổi 9)
   Chạy SAU 00_TaoDuLieuMau.sql, trên database luyện tập. Tạo các procedure / bảng CÓ LỖI CỐ Ý
   (tên bắt đầu zbt_loi_) để học viên tìm nguyên nhân và sửa. Không chạy trên database thật.
   ===================================================================================== */
SET NOCOUNT ON
GO
DECLARE @db sysname
SET @db = DB_NAME()
IF @db <> N'FBOSP2422_App'
BEGIN
    RAISERROR(N'Đang ở database %s. Script chỉ chạy trên database luyện tập FBOSP2422_App.', 16, 1, @db)
    SET NOEXEC ON
END
GO

/* 9.1 — Lọc NVBH không có tác dụng: OR không bọc ngoặc trong chuỗi @Key */
IF OBJECT_ID('zbt_loi_DoanhThuNVBH') IS NOT NULL DROP PROCEDURE zbt_loi_DoanhThuNVBH
GO
CREATE PROCEDURE zbt_loi_DoanhThuNVBH @DateFrom smalldatetime, @DateTo smalldatetime, @Nvbh varchar(8)
AS
BEGIN
    SET NOCOUNT ON
    DECLARE @Key nvarchar(4000), @q nvarchar(4000)
    SELECT TOP 0 so_ct, ngay_ct, ma_kh, ma_nvbh, status, t_tien2 INTO #hd FROM m81$000000
    SET @Key = 'a.status <> ''9'' or a.status <> ''*'''
    IF @Nvbh <> '' SET @Key = @Key + ' and a.ma_nvbh = ''' + @Nvbh + ''''
    SET @q = 'insert into #hd select so_ct, ngay_ct, ma_kh, ma_nvbh, status, t_tien2 from m81$%Partition a where %[' + @Key + ']%'
    EXEC FastBusiness$Partition$Execute @q, NULL, 'a.ngay_ct', @DateFrom, @DateTo, 1, 1
    SELECT * FROM #hd ORDER BY so_ct
END
GO

/* 9.2 — UPDATE ... FROM join một-nhiều: mỗi NCC chỉ lấy tiền của MỘT dòng chi tiết bất kỳ */
IF OBJECT_ID('zbt_loi_TongNhapNCC') IS NOT NULL DROP PROCEDURE zbt_loi_TongNhapNCC
GO
CREATE PROCEDURE zbt_loi_TongNhapNCC
AS
BEGIN
    SET NOCOUNT ON
    SELECT ma_kh, ten_kh, CAST(0 AS numeric(19,4)) AS tong_nhap INTO #ncc FROM dmkh WHERE cc_yn = 1
    UPDATE n SET n.tong_nhap = d.tien
    FROM #ncc n
    JOIN m74$202601 m ON m.ma_kh = n.ma_kh AND m.status IN ('1','2','3')
    JOIN d74$202601 d ON d.stt_rec = m.stt_rec
    SELECT * FROM #ncc ORDER BY ma_kh
END
GO

/* 9.3 — Báo cáo quý chỉ đọc một bảng kỳ (bỏ qua @DateFrom / @DateTo) */
IF OBJECT_ID('zbt_loi_DoanhThuQuy') IS NOT NULL DROP PROCEDURE zbt_loi_DoanhThuQuy
GO
CREATE PROCEDURE zbt_loi_DoanhThuQuy @DateFrom smalldatetime, @DateTo smalldatetime
AS
BEGIN
    SET NOCOUNT ON
    SELECT COUNT(*) AS so_hd, SUM(t_tien2) AS doanh_thu
    FROM m81$202601 a
    WHERE a.status NOT IN ('9', '*')
END
GO

/* 9.4 — Chia cho 0 với vật tư chưa bán */
IF OBJECT_ID('zbt_loi_GiaBanBQ') IS NOT NULL DROP PROCEDURE zbt_loi_GiaBanBQ
GO
CREATE PROCEDURE zbt_loi_GiaBanBQ
AS
BEGIN
    SET NOCOUNT ON
    SELECT v.ma_vt, SUM(ISNULL(d.tien2, 0)) AS tien, SUM(ISNULL(d.so_luong, 0)) AS sl,
           SUM(ISNULL(d.tien2, 0)) / SUM(ISNULL(d.so_luong, 0)) AS gia_bq
    FROM dmvt v LEFT JOIN d81$202601 d ON d.ma_vt = v.ma_vt
    GROUP BY v.ma_vt ORDER BY v.ma_vt
END
GO

/* 9.5 — Bảng dùng cho bài "Invalid column name" */
IF OBJECT_ID('zbt_loi_Bang') IS NOT NULL DROP TABLE zbt_loi_Bang
CREATE TABLE zbt_loi_Bang (ma varchar(16) NOT NULL PRIMARY KEY, ten nvarchar(100) NULL)
INSERT INTO zbt_loi_Bang VALUES ('A', N'Dòng A'), ('B', N'Dòng B')
GO
SET NOEXEC OFF
GO
SELECT name, type_desc FROM sys.objects WHERE name LIKE 'zbt[_]loi[_]%' ORDER BY name
GO
