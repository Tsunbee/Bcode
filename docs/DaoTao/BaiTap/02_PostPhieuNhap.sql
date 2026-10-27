/* =====================================================================================
   BÀI TẬP ĐÀO TẠO — 02. POST PHIẾU NHẬP KHO (PND, m74 / d74) CỦA DỮ LIỆU MẪU
   Chạy SAU 00_TaoDuLieuMau.sql (thứ tự với 01_PostHoaDonBan.sql không quan trọng), trên database luyện tập.

   Làm lại đúng chuỗi lệnh màn hình Phiếu nhập kho (inctpnd.aspx) chạy khi bấm Lưu (bắt bằng SQL Profiler),
   bỏ phần kiểm tra nhập liệu / cấp số chứng từ / phần customize của khách:
     i74 (UpdateInquiryTable) → c74 (UpdateGrandTable) → UpdateGeneral → tồn hiện thời (cdvt13…)
     → sổ kho r70 (Posting$Inventory, loại 1 = nhập) → sổ cái r00 (fs_PostIRTran + fs20_AfterUpdateGL)
     → AfterUpdate$IR → AfterUpdate$Transaction (khi m_instock_split = 1).
   status của PND: 0 lập chứng từ (không post) · 1 nhập kho (chỉ sổ kho) · 2 chuyển KTTH (sổ kho + chờ chuyển p00)
                   · 3 chuyển vào sổ cái (sổ kho + sổ cái) · * đã xoá (không post).
   Không chạy 2 lần: tồn hiện thời là cộng dồn. Muốn làm lại: chạy lại 00 rồi 01, 02.
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
IF EXISTS (SELECT 1 FROM c74$000000 WHERE stt_rec LIKE 'BT%')
BEGIN
    RAISERROR(N'Phiếu nhập mẫu đã được post rồi (có dòng BT… trong c74$000000). Chạy lại 00_TaoDuLieuMau.sql (tự dọn sổ cũ) trước khi post lại.', 16, 1)
    SET NOEXEC ON
END
IF NOT EXISTS (SELECT 1 FROM dmkh WHERE user_id0 = -1)
BEGIN
    RAISERROR(N'Chưa có dữ liệu mẫu — chạy 00_TaoDuLieuMau.sql trước.', 16, 1)
    SET NOEXEC ON
END
GO

SET XACT_ABORT ON
BEGIN TRAN

DECLARE @user int, @glMaster varchar(64), @glDetail varchar(64), @group varchar(128), @split char(1), @compact char(1),
        @t sysname, @q nvarchar(max), @set nvarchar(max)
SELECT @user = MIN(id) FROM FBOSP2422_Sys..userinfo
SELECT @glMaster = RTRIM(val) FROM options WHERE name = 'm_gl_master'
SELECT @glDetail = RTRIM(val) FROM options WHERE name = 'm_gl_detail'
SELECT @split   = ISNULL((SELECT RTRIM(val) FROM options WHERE name = 'm_instock_split'), '0')
SELECT @compact = ISNULL((SELECT RTRIM(val) FROM options WHERE name = 'm_compact_mode'), '0')
SELECT @group = ISNULL(groupby, '') FROM FBOSP2422_Sys..voucherinfo WHERE ma_ct = 'PND'
SET @group = ISNULL(@group, '')

/* ---------- 1. Chuẩn hoá như khi lưu từ web: cột không phải ngày thì '' / 0 thay vì NULL ---------- */
DECLARE n CURSOR LOCAL STATIC FOR
    SELECT name FROM sys.tables WHERE name LIKE '[md]74$2026__'
OPEN n
FETCH NEXT FROM n INTO @t
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @set = NULL
    SELECT @set = ISNULL(@set + ', ', '') + QUOTENAME(c.name) + ' = ISNULL(' + QUOTENAME(c.name) + ', ' +
                  CASE WHEN ty.name IN ('char','varchar','nchar','nvarchar') THEN '''''' ELSE '0' END + ')'
    FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(@t) AND c.is_nullable = 1 AND c.is_computed = 0
      AND ty.name IN ('char','varchar','nchar','nvarchar','numeric','decimal','int','tinyint','smallint','bigint','bit','money')
    SET @q = N'UPDATE ' + QUOTENAME(@t) + N' SET ' + @set + N' WHERE stt_rec LIKE ''BT%'''
    EXEC (@q)
    IF @t LIKE 'm74%'
        SET @q = N'UPDATE ' + QUOTENAME(@t) + N' SET loai_ct = ''3'', ma_gd = ''3'' WHERE stt_rec LIKE ''BT%'''     -- 3 = nhập mua ngoài
    ELSE
        SET @q = N'UPDATE ' + QUOTENAME(@t) + N' SET ma_nx = tk_du, pn_gia_tb = 0 WHERE stt_rec LIKE ''BT%'''
    EXEC (@q)
    FETCH NEXT FROM n INTO @t
END
CLOSE n
DEALLOCATE n

/* ---------- 2. Danh sách phiếu nhập mẫu ---------- */
CREATE TABLE #pn (stt_rec char(13), ky char(6), status char(1), loai_ct char(2), so_ct varchar(16))
SET @q = NULL
SELECT @q = ISNULL(@q + N' UNION ALL ', N'') + N'SELECT stt_rec, CONVERT(char(6), ngay_ct, 112), status, loai_ct, so_ct FROM ' + QUOTENAME(name) + N' WHERE stt_rec LIKE ''BT%'''
FROM sys.tables WHERE name LIKE 'm74$2026__'
SET @q = N'INSERT INTO #pn ' + @q
EXEC (@q)

/* ---------- 3. Post từng phiếu ---------- */
DECLARE @rec char(13), @ky char(6), @status char(1), @loaiCt char(2), @soCt varchar(16),
        @m varchar(32), @d varchar(32), @i varchar(32), @r70 varchar(32), @stock int, @vCode varchar(32), @qr varchar(8000), @rr tinyint,
        @f1 nvarchar(2000), @f2 nvarchar(2000), @external nvarchar(2000)
SELECT @rr = CAST(val AS tinyint) FROM options WHERE LOWER(name) = 'm_round_sl'
SELECT @f1 = 'gia_nt0, tien_nt0, gia0, tien0, gia01, gia_nt01, ct_dc, px_gia_dd, gia_nt21, gia21, gia_nt2, gia2, tien_nt2, tien2, cp_nt, cp, thue_suat_nk, nk_nt, nk, cp_thue_yn, thue_suat, thue_nt, thue, ck_nt, ck',
       @f2 = 'so_seri, ma_khon, tk_gv, tk_dt, tk_thue_nk, ma_tt, tk_thue_no, tk_thue_co, tk_ck, so_dh2, so_dh3, ma_nvbh, stt_rec_pn, stt_rec0pn, stt_rec_dc, so_ct0, so_seri0',
       @external = 'gia_nt1 = b.gia_nt, gia1 = b.gia, tien_xuat = 0, tien_nt_x = 0, tien_nhap = b.tien, tien_nt_n = b.tien_nt'

DECLARE p CURSOR LOCAL STATIC FOR SELECT stt_rec, ky, status, loai_ct, so_ct FROM #pn ORDER BY ky, so_ct
OPEN p
FETCH NEXT FROM p INTO @rec, @ky, @status, @loaiCt, @soCt
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @m = 'm74$' + @ky, @d = 'd74$' + @ky, @i = 'i74$' + @ky, @r70 = 'r70$' + @ky,
           @stock = 0, @vCode = 'PND' + CHAR(251) + @loaiCt + CHAR(251) + @loaiCt

    EXEC FastBusiness$App$Voucher$UpdateInquiryTable 'PND', @i, @m, @d, 'stt_rec', @rec,
         'ma_kh,loai_ct,ma_gd,ma_nt;#10$,#15$,#20$,#30$; , , , :ma_kho,ma_vt,tk_vt,ma_nx;#10$,#20$,#30$,#40$;d74,d74,d74,d74'
    EXEC FastBusiness$App$Voucher$UpdateGrandTable 'PND', 'c74$000000', @m, 'stt_rec', @rec, 1, ''
    EXEC FastBusiness$App$Voucher$UpdateGeneral 'PND', @m, @d, @i, @m, @rec

    IF @status IN ('1', '2', '3')
    BEGIN
        -- Tồn hiện thời (nhập)
        IF OBJECT_ID('tempdb..#currentStock') IS NOT NULL DROP TABLE #currentStock
        SELECT TOP 0 ma_kho, ma_vi_tri, ma_vt, ma_lo, so_luong AS sl_nhap, so_luong AS sl_xuat INTO #currentStock FROM d74$000000
        SET @q = N'INSERT INTO #currentStock SELECT ma_kho, ma_vi_tri, ma_vt, ma_lo, SUM(ROUND(so_luong * he_so, @rr)), 0
                   FROM ' + @d + N' WHERE stt_rec = @rec GROUP BY ma_kho, ma_vi_tri, ma_vt, ma_lo'
        EXEC sp_executesql @q, N'@rec char(13), @rr tinyint', @rec, @rr
        SET @qr = dbo.FastBusiness$Function$Voucher$GetSQLUpdateCurrentStock('r70$' + @ky, 'r90$' + @ky, @rec, @vCode, 'New', @stock)
        EXEC (@qr)

        -- Sổ kho r70 (loại 1 = nhập)
        EXEC FastBusiness$Voucher$Posting$Inventory @m, @d, @r70, @rec, 'PND', @f1, @f2, @external, NULL, @stock, 1

        -- Sổ cái: 2 = chờ chuyển (p00), 3 = ghi sổ cái ngay
        IF @status = '2'
        BEGIN
            SET @q = N'INSERT INTO p00$000000 SELECT stt_rec, ma_dvcs, ma_ct, ngay_ct, so_ct, t_tien_nt, t_tien, ma_nt, ty_gia, dien_giai, datetime2, user_id2 FROM ' + @m + N' WHERE stt_rec = @rec'
            EXEC sp_executesql @q, N'@rec char(13)', @rec
            EXEC dbo.fs_PostIRTran @m, @d, @glMaster, @glDetail, @rec, @group, 1
        END
        IF @status = '3'
        BEGIN
            EXEC dbo.fs_PostIRTran @m, @d, @glMaster, @glDetail, @rec, @group, 2
            EXEC fs20_AfterUpdateGL @rec, @m, @d, 'PND', @user
            SET @q = N'INSERT INTO r00$' + @ky + N' SELECT * FROM ct00 WHERE stt_rec = @rec'
            EXEC sp_executesql @q, N'@rec char(13)', @rec
        END

        IF @compact <> '1' EXEC FastBusiness$Voucher$AfterUpdate$IR @m, @d, @rec, 'New', @user
        IF @split = '1'    EXEC FastBusiness$Voucher$AfterUpdate$Transaction @d, 'c34$', 'm34$', 'd34$', @rec, 'New', @user
    END

    FETCH NEXT FROM p INTO @rec, @ky, @status, @loaiCt, @soCt
END
CLOSE p
DEALLOCATE p
DROP TABLE #pn

COMMIT
SET XACT_ABORT OFF     -- trả lại mặc định cho phiên
GO
SET NOEXEC OFF
GO

/* ---------- 4. Kiểm tra sau khi post ---------- */
SELECT N'c74 (sổ chứng từ)' AS so, COUNT(*) AS so_dong FROM c74$000000 WHERE stt_rec LIKE 'BT%'
UNION ALL SELECT N'i74 (tra cứu) Q1', (SELECT COUNT(*) FROM i74$202601 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM i74$202602 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM i74$202603 WHERE stt_rec LIKE 'BT%')
UNION ALL SELECT N'r70 nhập (sổ kho) Q1', (SELECT COUNT(*) FROM r70$202601 WHERE stt_rec LIKE 'BT%PND') + (SELECT COUNT(*) FROM r70$202602 WHERE stt_rec LIKE 'BT%PND') + (SELECT COUNT(*) FROM r70$202603 WHERE stt_rec LIKE 'BT%PND')
UNION ALL SELECT N'r00 nhập (sổ cái) Q1', (SELECT COUNT(*) FROM r00$202601 WHERE stt_rec LIKE 'BT%PND') + (SELECT COUNT(*) FROM r00$202602 WHERE stt_rec LIKE 'BT%PND') + (SELECT COUNT(*) FROM r00$202603 WHERE stt_rec LIKE 'BT%PND')
GO
