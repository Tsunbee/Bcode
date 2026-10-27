/* =====================================================================================
   BÀI TẬP ĐÀO TẠO FAST + BCODE — 00. TẠO DỮ LIỆU MẪU
   Chạy trên database App LUYỆN TẬP (mặc định FBOSP2422_App). Chạy lại nhiều lần được:
   script xoá dữ liệu mẫu cũ (kể cả các sổ đã post) rồi nạp lại. Sau đó chạy 01_PostHoaDonBan.sql, 02_PostPhieuNhap.sql để post hoá đơn bán, phiếu nhập.

   Dữ liệu mẫu được đánh dấu để xoá sạch bằng 99_XoaDuLieuMau.sql:
     - danh mục: user_id0 = -1
     - chứng từ: stt_rec bắt đầu bằng 'BT'
   Toàn bộ tên khách / vật tư là tên giả, dùng cho bài tập.
   ===================================================================================== */
SET NOCOUNT ON
GO
-- (1) Chặn chạy nhầm: đổi tên database ở dòng dưới nếu luyện trên database khác
DECLARE @db sysname
SET @db = DB_NAME()
IF @db <> N'FBOSP2422_App'
BEGIN
    RAISERROR(N'Đang ở database %s. Script chỉ chạy trên database luyện tập FBOSP2422_App (sửa dòng kiểm tra nếu cần).', 16, 1, @db)
    SET NOEXEC ON
END
GO
-- (2) Chặn chạy trên database đã có dữ liệu thật
IF EXISTS (SELECT 1 FROM dmkh WHERE ISNULL(user_id0, 0) <> -1)
   OR EXISTS (SELECT 1 FROM dmvt WHERE ISNULL(user_id0, 0) <> -1)
BEGIN
    RAISERROR(N'dmkh / dmvt đã có dữ liệu không phải dữ liệu mẫu — dừng để tránh lẫn với dữ liệu thật.', 16, 1)
    SET NOEXEC ON
END
GO

BEGIN TRAN

/* ---------- Xoá dữ liệu mẫu cũ (cả các sổ đã post bằng 01_PostHoaDonBan.sql) ---------- */
DECLARE @t sysname, @q nvarchar(max)
DECLARE c CURSOR LOCAL STATIC FOR
    SELECT t.name FROM sys.tables t JOIN sys.columns col ON col.object_id = t.object_id AND col.name = 'stt_rec'
OPEN c
FETCH NEXT FROM c INTO @t
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @q = N'IF EXISTS (SELECT 1 FROM ' + QUOTENAME(@t) + N' WHERE stt_rec LIKE ''BT%'') DELETE FROM ' + QUOTENAME(@t) + N' WHERE stt_rec LIKE ''BT%'''
    EXEC (@q)
    FETCH NEXT FROM c INTO @t
END
CLOSE c
DEALLOCATE c
DECLARE s CURSOR LOCAL STATIC FOR
    SELECT name FROM sys.tables
    WHERE name IN ('cdvt13','cdvt213','cdvt73','cdvt93','cdvitri13','cdvitri213','cdlo13','cdlo213','cdklo13','cdklo213')
OPEN s
FETCH NEXT FROM s INTO @t
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @q = N'DELETE FROM ' + QUOTENAME(@t) + N' WHERE ma_vt IN (SELECT ma_vt FROM dmvt WHERE user_id0 = -1)'
    EXEC (@q)
    FETCH NEXT FROM s INTO @t
END
CLOSE s
DEALLOCATE s
DELETE FROM dmkh   WHERE user_id0 = -1
DELETE FROM dmvt   WHERE user_id0 = -1
DELETE FROM dmkho  WHERE user_id0 = -1
DELETE FROM dmbp   WHERE user_id0 = -1
DELETE FROM dmnvbh WHERE user_id0 = -1

/* ---------- Danh mục ---------- */
DECLARE @tk131 varchar(16), @tk331 varchar(16), @now datetime
SELECT @tk131 = MIN(tk) FROM dmtk WHERE tk LIKE '131%' AND loai_tk = 1
SELECT @tk331 = MIN(tk) FROM dmtk WHERE tk LIKE '331%' AND loai_tk = 1
SET @now = GETDATE()

INSERT INTO dmnvbh (ma_nvbh, ten_nvbh, ten_nvbh2, status, datetime0, datetime2, user_id0, user_id2) VALUES
 ('NV01', N'Nguyễn Văn An',    N'Nguyen Van An',    '1', @now, @now, -1, -1),
 ('NV02', N'Trần Thị Bình',    N'Tran Thi Binh',    '1', @now, @now, -1, -1),
 ('NV03', N'Lê Hoàng Cường',   N'Le Hoang Cuong',   '1', @now, @now, -1, -1)

INSERT INTO dmbp (ma_bp, ten_bp, ten_bp2, status, datetime0, datetime2, user_id0, user_id2) VALUES
 ('BP01', N'Phòng kinh doanh', N'Sales Department',      '1', @now, @now, -1, -1),
 ('BP02', N'Phòng kế toán',    N'Accounting Department', '1', @now, @now, -1, -1),
 ('BP03', N'Phòng kho vận',    N'Logistics Department',  '1', @now, @now, -1, -1)

INSERT INTO dmkho (ma_dvcs, ma_kho, ten_kho, ten_kho2, status, datetime0, datetime2, user_id0, user_id2) VALUES
 ('CTY', 'KHO1', N'Kho tổng',      N'Main Warehouse',   '1', @now, @now, -1, -1),
 ('CTY', 'KHO2', N'Kho chi nhánh', N'Branch Warehouse', '1', @now, @now, -1, -1)

INSERT INTO dmkh (ma_kh, ten_kh, ten_kh2, kh_yn, cc_yn, ma_so_thue, dia_chi, ma_nvbh, tk, status, datetime0, datetime2, user_id0, user_id2) VALUES
 ('KH001', N'Công ty TNHH Minh Phát',            N'Minh Phat Co., Ltd',          1, 0, '0301000001', N'12 Lê Lợi, Q.1, TP.HCM',          'NV01', @tk131, '1', @now, @now, -1, -1),
 ('KH002', N'Công ty CP Thương mại Hoàng Long',  N'Hoang Long Trading JSC',      1, 0, '0301000002', N'45 Nguyễn Huệ, Q.1, TP.HCM',      'NV01', @tk131, '1', @now, @now, -1, -1),
 ('KH003', N'Công ty TNHH Sao Mai',              N'Sao Mai Co., Ltd',            1, 0, '0301000003', N'8 Trần Phú, TP. Nha Trang',       'NV02', @tk131, '1', @now, @now, -1, -1),
 ('KH004', N'Cửa hàng Bình An',                  N'Binh An Store',               1, 0, NULL,         N'21 Hai Bà Trưng, TP. Huế',        'NV02', @tk131, '1', @now, @now, -1, -1),
 ('KH005', N'Công ty CP Xây dựng Đông Á',        N'Dong A Construction JSC',     1, 0, '0301000005', N'99 Điện Biên Phủ, Q.3, TP.HCM',   'NV01', @tk131, '1', @now, @now, -1, -1),
 ('KH006', N'Công ty TNHH Phú Thịnh',            N'Phu Thinh Co., Ltd',          1, 0, '0301000006', N'5 Lý Thường Kiệt, Hà Nội',        'NV02', @tk131, '1', @now, @now, -1, -1),
 ('KH007', N'Siêu thị Hạnh Phúc',                N'Hanh Phuc Supermarket',       1, 0, '0301000007', N'30 Quang Trung, TP. Đà Nẵng',     NULL,   @tk131, '1', @now, @now, -1, -1),
 ('KH008', N'Công ty TNHH Việt Tiến',            N'Viet Tien Co., Ltd',          1, 0, '0301000008', N'17 Võ Văn Tần, Q.3, TP.HCM',      'NV01', @tk131, '0', @now, @now, -1, -1),
 ('KH009', N'Công ty TNHH Sao Mai',              N'Sao Mai Co., Ltd (HN)',       1, 0, '0301000009', N'2 Phố Huế, Hà Nội',               'NV02', @tk131, '1', @now, @now, -1, -1),
 ('KH010', N'Nhà phân phối Thành Công',          N'Thanh Cong Distributor',      1, 0, '0301000010', N'66 Cách Mạng Tháng 8, TP. Cần Thơ','NV01', @tk131, '1', @now, @now, -1, -1),
 ('NCC01', N'Công ty TNHH Nguyên liệu Á Châu',   N'A Chau Materials Co., Ltd',   0, 1, '0302000001', N'KCN Tân Bình, TP.HCM',            NULL,   @tk331, '1', @now, @now, -1, -1),
 ('NCC02', N'Công ty CP Bao bì Tân Tiến',        N'Tan Tien Packaging JSC',      0, 1, '0302000002', N'KCN Sóng Thần, Bình Dương',       NULL,   @tk331, '1', @now, @now, -1, -1)

INSERT INTO dmvt (ma_vt, ma_vt2, ten_vt, ten_vt2, dvt, vt_ton_kho, tk_vt, status, datetime0, datetime2, user_id0, user_id2) VALUES
 ('VT001', 'VT001', N'Gạo ST25',            N'ST25 Rice',            N'kg',    1, '1561', '1', @now, @now, -1, -1),
 ('VT002', 'VT002', N'Đường trắng',         N'White Sugar',          N'kg',    1, '1561', '1', @now, @now, -1, -1),
 ('VT003', 'VT003', N'Dầu ăn 1 lít',        N'Cooking Oil 1L',       N'chai',  1, '1561', '1', @now, @now, -1, -1),
 ('VT004', 'VT004', N'Nước mắm 500 ml',     N'Fish Sauce 500ml',     N'chai',  1, '1561', '1', @now, @now, -1, -1),
 ('VT005', 'VT005', N'Bột mì',              N'Wheat Flour',          N'kg',    1, '1561', '1', @now, @now, -1, -1),
 ('VT006', 'VT006', N'Mì gói (thùng 30)',   N'Instant Noodles (30)', N'thùng', 1, '1561', '1', @now, @now, -1, -1),
 ('VT007', 'VT007', N'Sữa hộp (thùng 48)',  N'Boxed Milk (48)',      N'thùng', 1, '1561', '1', @now, @now, -1, -1),
 ('VT008', 'VT008', N'Cà phê rang',         N'Roasted Coffee',       N'kg',    1, '1561', '1', @now, @now, -1, -1)

UPDATE dmkh SET du13 = 0, du_nt13 = 0 WHERE user_id0 = -1      -- dư nợ hiện thời: 0 như khi tạo từ màn hình

/* ---------- Chứng từ: gom vào bảng tạm rồi đẩy vào đúng bảng kỳ ---------- */
CREATE TABLE #ct (ma_ct char(3), so_ct varchar(16), ngay smalldatetime, ma_kh varchar(16), ma_nvbh varchar(8), ma_kho varchar(16),
                  status char(1), dien_giai nvarchar(200), line int, ma_vt varchar(16), sl numeric(19,4), gia numeric(19,4), gia_ban numeric(19,4))

-- Phiếu nhập kho PND (gia = giá nhập)
INSERT INTO #ct (ma_ct, so_ct, ngay, ma_kh, ma_kho, status, dien_giai, line, ma_vt, sl, gia) VALUES
 ('PND','PN0001','20260105','NCC01','KHO1','3',N'Nhập gạo, đường đầu tháng',1,'VT001',1000,20000),
 ('PND','PN0001','20260105','NCC01','KHO1','3',N'Nhập gạo, đường đầu tháng',2,'VT002', 500,18000),
 ('PND','PN0002','20260112','NCC02','KHO1','3',N'Nhập dầu ăn, nước mắm',   1,'VT003', 200,45000),
 ('PND','PN0002','20260112','NCC02','KHO1','3',N'Nhập dầu ăn, nước mắm',   2,'VT004', 300,25000),
 ('PND','PN0003','20260120','NCC01','KHO2','3',N'Nhập kho chi nhánh',      1,'VT001', 400,20500),
 ('PND','PN0003','20260120','NCC01','KHO2','3',N'Nhập kho chi nhánh',      2,'VT005', 600,15000),
 ('PND','PN0004','20260203','NCC01','KHO1','3',N'Nhập mì, sữa',            1,'VT006', 100,180000),
 ('PND','PN0004','20260203','NCC01','KHO1','3',N'Nhập mì, sữa',            2,'VT007',  80,320000),
 ('PND','PN0005','20260215','NCC02','KHO1','0',N'Phiếu mới lập, chưa nhập kho',1,'VT001',800,21000),
 ('PND','PN0005','20260215','NCC02','KHO1','0',N'Phiếu mới lập, chưa nhập kho',2,'VT003',150,46000),
 ('PND','PN0006','20260225','NCC01','KHO2','*',N'Phiếu đã xoá',            1,'VT002', 300,18500),
 ('PND','PN0007','20260304','NCC02','KHO1','3',N'Nhập bổ sung tháng 3',    1,'VT004', 200,25500),
 ('PND','PN0007','20260304','NCC02','KHO1','3',N'Nhập bổ sung tháng 3',    2,'VT005', 400,15200),
 ('PND','PN0007','20260304','NCC02','KHO1','3',N'Nhập bổ sung tháng 3',    3,'VT006',  50,182000),
 ('PND','PN0008','20260318','NCC01','KHO2','3',N'Nhập sữa kho chi nhánh',  1,'VT007',  60,325000)

-- Hoá đơn bán HDA (gia_ban = giá bán; gia = giá vốn ước tính)
INSERT INTO #ct (ma_ct, so_ct, ngay, ma_kh, ma_nvbh, ma_kho, status, dien_giai, line, ma_vt, sl, gia_ban, gia) VALUES
 ('HDA','HD0001','20260108','KH001','NV01','KHO1','2',N'Bán gạo, đường',      1,'VT001',300,28000,20000),
 ('HDA','HD0001','20260108','KH001','NV01','KHO1','2',N'Bán gạo, đường',      2,'VT002',100,24000,18000),
 ('HDA','HD0002','20260115','KH002','NV01','KHO1','2',N'Bán dầu ăn',          1,'VT003', 50,60000,45000),
 ('HDA','HD0003','20260122','KH003','NV02','KHO1','2',N'Bán gạo, nước mắm',   1,'VT001',200,28500,20000),
 ('HDA','HD0003','20260122','KH003','NV02','KHO1','2',N'Bán gạo, nước mắm',   2,'VT004',100,32000,25000),
 ('HDA','HD0004','20260128','KH004','NV02','KHO2','2',N'Bán bột mì',          1,'VT005',150,20000,15000),
 ('HDA','HD0005','20260206','KH001','NV01','KHO1','2',N'Bán mì gói',          1,'VT006', 30,230000,180000),
 ('HDA','HD0006','20260214','KH005','NV01','KHO1','2',N'Bán sữa, gạo',        1,'VT007', 20,400000,320000),
 ('HDA','HD0006','20260214','KH005','NV01','KHO1','2',N'Bán sữa, gạo',        2,'VT001',100,29000,20500),
 ('HDA','HD0007','20260220','KH006','NV02','KHO2','9',N'Hoá đơn đã huỷ',      1,'VT002',200,24500,18000),
 ('HDA','HD0008','20260227','KH007',NULL,  'KHO1','2',N'Bán dầu ăn siêu thị', 1,'VT003', 40,61000,45000),
 ('HDA','HD0009','20260305','KH009','NV02','KHO1','2',N'Bán nước mắm',        1,'VT004', 80,32500,25000),
 ('HDA','HD0010','20260312','KH001','NV01','KHO1','2',N'Bán gạo, bột mì',     1,'VT001',250,29500,20500),
 ('HDA','HD0010','20260312','KH001','NV01','KHO1','2',N'Bán gạo, bột mì',     2,'VT005',100,20500,15000),
 ('HDA','HD0011','20260325','KH002','NV01','KHO2','2',N'Bán sữa',             1,'VT007', 15,405000,325000)

-- stt_rec = 'BT' + 8 số + mã chứng từ (đủ 13 ký tự)
SELECT so_ct, ma_ct, 'BT' + RIGHT('00000000' + CAST(ROW_NUMBER() OVER (ORDER BY ma_ct DESC, so_ct) AS varchar(8)), 8) + ma_ct AS stt_rec
INTO #rec FROM (SELECT DISTINCT so_ct, ma_ct FROM #ct) x

DECLARE @ky char(6), @sql nvarchar(max)
DECLARE k CURSOR LOCAL FAST_FORWARD FOR SELECT DISTINCT CONVERT(char(6), ngay, 112) FROM #ct
OPEN k
FETCH NEXT FROM k INTO @ky
WHILE @@FETCH_STATUS = 0
BEGIN
    -- Phiếu nhập: m74 / d74
    SET @sql = N'
    INSERT INTO [m74$' + @ky + N'] (stt_rec, ma_dvcs, ma_ct, ngay_lct, ngay_ct, so_ct, ma_nt, ty_gia, ma_kh, dien_giai,
        t_so_luong, t_tien_nt, t_tien, nam, ky, status, datetime0, datetime2, user_id0, user_id2)
    SELECT r.stt_rec, ''CTY'', c.ma_ct, c.ngay, c.ngay, c.so_ct, ''VND'', 1, c.ma_kh, MAX(c.dien_giai),
        SUM(c.sl), SUM(c.sl * c.gia), SUM(c.sl * c.gia), YEAR(c.ngay), MONTH(c.ngay), c.status, GETDATE(), GETDATE(), -1, -1
    FROM #ct c JOIN #rec r ON r.so_ct = c.so_ct
    WHERE c.ma_ct = ''PND'' AND CONVERT(char(6), c.ngay, 112) = @ky
    GROUP BY r.stt_rec, c.ma_ct, c.ngay, c.so_ct, c.ma_kh, c.status

    INSERT INTO [d74$' + @ky + N'] (stt_rec, stt_rec0, ma_ct, ngay_ct, so_ct, ma_vt, dvt, he_so, ma_kho, tk_vt, tk_du,
        so_luong, gia_nt, gia, tien_nt, tien, line_nbr)
    SELECT r.stt_rec, RIGHT(''00'' + CAST(c.line AS varchar(3)), 3), c.ma_ct, c.ngay, c.so_ct, c.ma_vt, v.dvt, 1, c.ma_kho, ''1561'', @tk331,
        c.sl, c.gia, c.gia, c.sl * c.gia, c.sl * c.gia, c.line
    FROM #ct c JOIN #rec r ON r.so_ct = c.so_ct JOIN dmvt v ON v.ma_vt = c.ma_vt
    WHERE c.ma_ct = ''PND'' AND CONVERT(char(6), c.ngay, 112) = @ky

    -- Hoá đơn bán: m81 / d81 (tien2 = tiền hàng bán, tien = giá vốn, thuế 10%)
    INSERT INTO [m81$' + @ky + N'] (stt_rec, ma_dvcs, ma_ct, ngay_lct, ngay_ct, so_ct, ma_nt, ty_gia, ma_kh, dien_giai, tk,
        t_so_luong, t_tien_nt2, t_tien2, t_tien_nt, t_tien, t_thue_nt, t_thue, t_tt_nt, t_tt, ma_nvbh, thue_suat, nam, ky, status,
        datetime0, datetime2, user_id0, user_id2)
    SELECT r.stt_rec, ''CTY'', c.ma_ct, c.ngay, c.ngay, c.so_ct, ''VND'', 1, c.ma_kh, MAX(c.dien_giai), @tk131,
        SUM(c.sl), SUM(c.sl * c.gia_ban), SUM(c.sl * c.gia_ban), SUM(c.sl * c.gia), SUM(c.sl * c.gia),
        SUM(c.sl * c.gia_ban) * 0.1, SUM(c.sl * c.gia_ban) * 0.1, SUM(c.sl * c.gia_ban) * 1.1, SUM(c.sl * c.gia_ban) * 1.1,
        c.ma_nvbh, 10, YEAR(c.ngay), MONTH(c.ngay), c.status, GETDATE(), GETDATE(), -1, -1
    FROM #ct c JOIN #rec r ON r.so_ct = c.so_ct
    WHERE c.ma_ct = ''HDA'' AND CONVERT(char(6), c.ngay, 112) = @ky
    GROUP BY r.stt_rec, c.ma_ct, c.ngay, c.so_ct, c.ma_kh, c.ma_nvbh, c.status

    INSERT INTO [d81$' + @ky + N'] (stt_rec, stt_rec0, ma_ct, ngay_ct, so_ct, ma_vt, dvt, he_so, ma_kho, tk_vt, so_luong,
        gia_nt2, gia2, tien_nt2, tien2, gia_nt, gia, tien_nt, tien, thue_suat, thue_nt, thue, tt_nt, tt, ma_nvbh_i, line_nbr)
    SELECT r.stt_rec, RIGHT(''00'' + CAST(c.line AS varchar(3)), 3), c.ma_ct, c.ngay, c.so_ct, c.ma_vt, v.dvt, 1, c.ma_kho, ''1561'', c.sl,
        c.gia_ban, c.gia_ban, c.sl * c.gia_ban, c.sl * c.gia_ban, c.gia, c.gia, c.sl * c.gia, c.sl * c.gia,
        10, c.sl * c.gia_ban * 0.1, c.sl * c.gia_ban * 0.1, c.sl * c.gia_ban * 1.1, c.sl * c.gia_ban * 1.1, c.ma_nvbh, c.line
    FROM #ct c JOIN #rec r ON r.so_ct = c.so_ct JOIN dmvt v ON v.ma_vt = c.ma_vt
    WHERE c.ma_ct = ''HDA'' AND CONVERT(char(6), c.ngay, 112) = @ky'
    EXEC sp_executesql @sql, N'@ky char(6), @tk131 varchar(16), @tk331 varchar(16)', @ky, @tk131, @tk331
    FETCH NEXT FROM k INTO @ky
END
CLOSE k
DEALLOCATE k

DROP TABLE #ct
DROP TABLE #rec
COMMIT
GO
SET NOEXEC OFF
GO
-- Kiểm tra nhanh
SELECT N'dmkh' AS bang, COUNT(*) AS so_dong FROM dmkh WHERE user_id0 = -1
UNION ALL SELECT N'dmvt', COUNT(*) FROM dmvt WHERE user_id0 = -1
UNION ALL SELECT N'dmkho', COUNT(*) FROM dmkho WHERE user_id0 = -1
UNION ALL SELECT N'dmnvbh', COUNT(*) FROM dmnvbh WHERE user_id0 = -1
UNION ALL SELECT N'dmbp', COUNT(*) FROM dmbp WHERE user_id0 = -1
UNION ALL SELECT N'Phiếu nhập (m74 Q1/2026)', (SELECT COUNT(*) FROM [m74$202601] WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM [m74$202602] WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM [m74$202603] WHERE stt_rec LIKE 'BT%')
UNION ALL SELECT N'Hoá đơn bán (m81 Q1/2026)', (SELECT COUNT(*) FROM [m81$202601] WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM [m81$202602] WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM [m81$202603] WHERE stt_rec LIKE 'BT%')
GO
