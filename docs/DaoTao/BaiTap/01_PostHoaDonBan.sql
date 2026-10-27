/* =====================================================================================
   BÀI TẬP ĐÀO TẠO — 01. POST HOÁ ĐƠN BÁN (HDA, m81 / d81) CỦA DỮ LIỆU MẪU
   Chạy SAU 00_TaoDuLieuMau.sql, trên database luyện tập.

   Làm lại đúng chuỗi lệnh mà màn hình Hoá đơn bán (socthda.aspx) chạy khi bấm Lưu (bắt bằng SQL Profiler),
   bỏ phần kiểm tra nhập liệu / cấp số chứng từ / phần customize của khách:
     ctgt21 → ds_PostSVDiscount → hddt00/01 → i81 (UpdateInquiryTable) → c81 (UpdateGrandTable) → UpdateGeneral
     → tồn hiện thời (cdvt13…) → sổ kho r70 (Posting$Inventory) → sổ cái r00 (fs_PostSVTran + fs20_AfterUpdateGL)
     → sổ thuế r20 → sổ kho thực tế r90 (hoá đơn kiêm phiếu xuất) → AfterUpdate$SV → dư nợ hiện thời (dmkh.du13).
   Hoá đơn status 9 (huỷ) / * (xoá): chỉ ghi c81, i81 — không ghi sổ.
   Không chạy 2 lần: các bước tồn / dư nợ hiện thời là cộng dồn. Muốn làm lại: chạy lại 00 rồi 01, 02.
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
IF EXISTS (SELECT 1 FROM c81$000000 WHERE stt_rec LIKE 'BT%')
BEGIN
    RAISERROR(N'Hoá đơn mẫu đã được post rồi (có dòng BT… trong c81$000000). Chạy lại 00_TaoDuLieuMau.sql (tự dọn sổ cũ) trước khi post lại.', 16, 1)
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

DECLARE @sys sysname, @user int, @glMaster varchar(64), @glDetail varchar(64), @group varchar(128), @useEI char(1),
        @tkDt varchar(16), @tkGv varchar(16), @tkThue varchar(16), @t sysname, @q nvarchar(max), @set nvarchar(max)
SET @sys = N'FBOSP2422_Sys'
SELECT @user = MIN(id) FROM FBOSP2422_Sys..userinfo
SELECT @glMaster = RTRIM(val) FROM options WHERE name = 'm_gl_master'
SELECT @glDetail = RTRIM(val) FROM options WHERE name = 'm_gl_detail'
SELECT @useEI = ISNULL((SELECT RTRIM(val) FROM options WHERE name = 'm_sd_hddt'), '0')
SELECT @group = dbo.FastBusiness$MergeList(ISNULL(a.groupby, ''), ISNULL(b.groupby, ''), ',')
FROM FBOSP2422_Sys..voucherinfo a JOIN dmct b ON a.ma_ct = b.ma_ct WHERE a.ma_ct = 'HDA'
SELECT @tkDt   = MIN(tk) FROM dmtk WHERE tk LIKE '5111%'  AND loai_tk = 1
SELECT @tkGv   = MIN(tk) FROM dmtk WHERE tk LIKE '632%'   AND loai_tk = 1
SELECT @tkThue = MIN(tk) FROM dmtk WHERE tk LIKE '33311%' AND loai_tk = 1

/* ---------- 1. Chuẩn hoá như khi lưu từ web: cột không phải ngày thì '' / 0 thay vì NULL ---------- */
UPDATE dmkh SET du13 = ISNULL(du13, 0), du_nt13 = ISNULL(du_nt13, 0) WHERE user_id0 = -1     -- dư nợ hiện thời (NULL + số = NULL)
DECLARE n CURSOR LOCAL STATIC FOR
    SELECT name FROM sys.tables WHERE name LIKE '[md]81$2026__'
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
    IF @t LIKE 'm81%'
        SET @q = N'UPDATE ' + QUOTENAME(@t) + N' SET ma_gd = ''2'', loai_ct = ''2'', tk_thue_no = tk, tk_thue_co = @tkThue,
                   t_tc_tien2 = t_tien2, t_tc_tien_nt2 = t_tien_nt2, t_tc_thue = t_thue, t_tc_thue_nt = t_thue_nt,
                   t_tc_tt = t_tt, t_tc_tt_nt = t_tt_nt WHERE stt_rec LIKE ''BT%'''
    ELSE
        SET @q = N'UPDATE ' + QUOTENAME(@t) + N' SET ma_thue = ''10'', tk_thue = @tkThue, tk_dt = @tkDt, tk_gv = @tkGv,
                   loai = '''', km_yn = 0, line_type = 0, ck_nt0 = ck_nt, ck0 = ck, thue_nt0 = thue_nt, thue0 = thue
                   WHERE stt_rec LIKE ''BT%'''
    EXEC sp_executesql @q, N'@tkDt varchar(16), @tkGv varchar(16), @tkThue varchar(16)', @tkDt, @tkGv, @tkThue
    FETCH NEXT FROM n INTO @t
END
CLOSE n
DEALLOCATE n

/* ---------- 2. Danh sách hoá đơn mẫu ---------- */
CREATE TABLE #hd (stt_rec char(13), ky char(6), status char(1), loai_ct char(2), ma_gd char(2), so_ct varchar(16))
SET @q = NULL
SELECT @q = ISNULL(@q + N' UNION ALL ', N'') + N'SELECT stt_rec, CONVERT(char(6), ngay_ct, 112), status, loai_ct, ma_gd, so_ct FROM ' + QUOTENAME(name) + N' WHERE stt_rec LIKE ''BT%'''
FROM sys.tables WHERE name LIKE 'm81$2026__'
SET @q = N'INSERT INTO #hd ' + @q
EXEC (@q)

/* ---------- 3. Post từng hoá đơn ---------- */
DECLARE @rec char(13), @ky char(6), @status char(1), @loaiCt char(2), @maGd char(2), @soCt varchar(16),
        @m varchar(32), @d varchar(32), @i varchar(32), @r70 varchar(32), @r00 varchar(32), @stock tinyint, @qr varchar(8000), @rr tinyint,
        @external nvarchar(4000), @statement nvarchar(4000)
SELECT @rr = CAST(val AS tinyint) FROM options WHERE LOWER(name) = 'm_round_sl'

DECLARE h CURSOR LOCAL STATIC FOR SELECT stt_rec, ky, status, loai_ct, ma_gd, so_ct FROM #hd ORDER BY ky, so_ct
OPEN h
FETCH NEXT FROM h INTO @rec, @ky, @status, @loaiCt, @maGd, @soCt
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @m = 'm81$' + @ky, @d = 'd81$' + @ky, @i = 'i81$' + @ky, @r70 = 'r70$' + @ky, @r00 = 'r00$' + @ky, @stock = CASE WHEN @loaiCt = '2' THEN 1 ELSE 0 END

    -- Thông tin người mua trên hoá đơn GTGT
    SET @q = N'INSERT INTO ctgt21 (stt_rec, ngay_ct, ngay_lct, ten_kh, dia_chi, ma_so_thue)
               SELECT a.stt_rec, a.ngay_ct, a.ngay_lct, k.ten_kh, ISNULL(k.dia_chi, ''''), ISNULL(k.ma_so_thue, '''')
               FROM ' + @m + N' a JOIN dmkh k ON k.ma_kh = a.ma_kh WHERE a.stt_rec = @rec'
    EXEC sp_executesql @q, N'@rec char(13)', @rec

    EXEC dbo.ds_PostSVDiscount @ky, '$partition$previous', 'HDA', @rec, '', @status, 'New', @user, 1, @sys

    IF @useEI = '1'
    BEGIN
        SET @q = N'INSERT INTO hddt00$' + @ky + N' (stt_rec, ngay_ct, ma_ct, ma_dvcs, mau_hddt, so_seri_hddt, so_ct_hddt, tinh_trang_hddt, trang_thai_tt, xac_thuc, status, datetime0, datetime2, user_id0, user_id2)
                   SELECT stt_rec, ngay_ct, ma_ct, ma_dvcs, '''', '''', '''', ''0'', ''0'', 1, status, datetime0, datetime0, @user, @user FROM ' + @m + N' WHERE stt_rec = @rec
                   INSERT INTO hddt01$' + @ky + N' (stt_rec, so_bi_mat) SELECT @rec, '''''
        EXEC sp_executesql @q, N'@rec char(13), @user int', @rec, @user
    END

    EXEC FastBusiness$App$Voucher$UpdateInquiryTable 'HDA', @i, @m, @d, 'stt_rec', @rec,
         'ma_kh,loai_ct,ma_nt;#10$,#15$,#20$; , , :ma_kho,ma_vt,tk_vt,tk_gv,tk_dt;#10$,#20$,#30$,#40$,#50$;d81,d81,d81,d81,d81'
    EXEC FastBusiness$App$Voucher$UpdateGrandTable 'HDA', 'c81$000000', @m, 'stt_rec', @rec, 1, ''
    EXEC FastBusiness$App$Voucher$UpdateGeneral 'HDA', @m, @d, @i, @m, @rec

    IF @status IN ('1', '2') AND @maGd <> '8'
    BEGIN
        -- Tồn hiện thời
        IF OBJECT_ID('tempdb..#currentStock') IS NOT NULL DROP TABLE #currentStock
        SELECT TOP 0 ma_kho, ma_vi_tri, ma_vt, ma_lo, so_luong AS sl_nhap, so_luong AS sl_xuat INTO #currentStock FROM d81$000000
        SET @q = N'INSERT INTO #currentStock SELECT ma_kho, ma_vi_tri, ma_vt, ma_lo, 0, SUM(ROUND(so_luong * he_so, @rr))
                   FROM ' + @d + N' WHERE stt_rec = @rec GROUP BY ma_kho, ma_vi_tri, ma_vt, ma_lo'
        EXEC sp_executesql @q, N'@rec char(13), @rr tinyint', @rec, @rr
        SET @qr = dbo.FastBusiness$Function$Voucher$GetSQLUpdateCurrentStock('r70$' + @ky, 'r90$' + @ky, @rec, 'HDA', 'New', @stock)
        EXEC (@qr)

        -- Sổ kho r70 (xuất)
        SET @statement = CHAR(13) + 'update #in set ma_nx = b.ma_nx from #in a join dmnx b on a.ma_nx = b.tk where b.ma_nx is not null; delete #in where s4 = 1;'
        SET @external = '
tk_du = b.tk_gv, ma_nx = b.tk_gv,
gia2 = case b.km_yn when 0 then b.gia2 else 0 end, gia_nt2 = case b.km_yn when 0 then b.gia_nt2 else 0 end,
tien2 = case b.km_yn when 0 then b.tien2 else 0 end, tien_nt2 = case b.km_yn when 0 then b.tien_nt2 else 0 end,
gia1 = b.gia, gia_nt1 = b.gia_nt, gia21 = case b.km_yn when 0 then b.gia2 else 0 end, gia_nt21 = case b.km_yn when 0 then b.gia_nt2 else 0 end, tien_nhap = 0, tien_nt_n = 0,
tien_xuat = b.tien, tien_nt_x = b.tien_nt, s4 = 0, tk_thue_co = b.tk_thue
'
        EXEC FastBusiness$Voucher$Posting$Inventory @m, @d, @r70, @rec, 'HDA',
             'pn_gia_tb, ct_dc, gia_nt01, gia01, gia_nt0, gia0, tien_nt0, tien0, cp_thue_yn',
             'so_seri, ma_khon, ma_vi_trin, so_dh2, so_dh3, ma_nv, stt_rec_px, stt_rec0px, stt_rec_dc, so_seri0',
             @external, @statement, @stock, 2, 'a.loai not in (''05'', ''08'', ''30'')'

        -- Sổ cái r00 (status 1 = chờ chuyển KTTH: chỉ đưa vào p00)
        IF @status = '1'
        BEGIN
            SET @q = N'INSERT INTO p00$000000 SELECT stt_rec, ma_dvcs, ma_ct, ngay_ct, so_ct, t_tt_nt, t_tt, ma_nt, ty_gia, dien_giai, datetime2, user_id2 FROM ' + @m + N' WHERE stt_rec = @rec'
            EXEC sp_executesql @q, N'@rec char(13)', @rec
            EXEC fs_PostSVTran @m, @d, @glMaster, @glDetail, @rec, @group, 1, 'None'
        END
        ELSE
        BEGIN
            EXEC fs_PostSVTran @m, @d, @glMaster, @glDetail, @rec, @group, 2, 'None'
            EXEC fs20_AfterUpdateGL @rec, @m, @d, 'HDA', @user
            SET @q = N'INSERT INTO r00$' + @ky + N' SELECT * FROM ct00 WHERE stt_rec = @rec'
            EXEC sp_executesql @q, N'@rec char(13)', @rec
        END

        SET @q = N'INSERT INTO r20$' + @ky + N' SELECT * FROM ctgt20 WHERE stt_rec = @rec'
        IF @stock = 1 SET @q = @q + N'
                   INSERT INTO r90$' + @ky + N' SELECT * FROM ct90 WHERE stt_rec = @rec'
        EXEC sp_executesql @q, N'@rec char(13)', @rec

        EXEC FastBusiness$Voucher$AfterUpdate$SV @m, @d, @rec, 'New', @user
        EXEC FastBusiness$Voucher$AfterUpdate$CurrentCustomerBalance @r00, @rec, 'HDA', 'New'
    END

    FETCH NEXT FROM h INTO @rec, @ky, @status, @loaiCt, @maGd, @soCt
END
CLOSE h
DEALLOCATE h
DROP TABLE #hd

COMMIT
SET XACT_ABORT OFF     -- trả lại mặc định cho phiên (SET này giữ nguyên tới hết kết nối)
GO
SET NOEXEC OFF
GO

/* ---------- 4. Kiểm tra sau khi post ---------- */
SELECT N'c81 (sổ chứng từ)' AS so, COUNT(*) AS so_dong FROM c81$000000 WHERE stt_rec LIKE 'BT%'
UNION ALL SELECT N'i81 (tra cứu) Q1',  (SELECT COUNT(*) FROM i81$202601 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM i81$202602 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM i81$202603 WHERE stt_rec LIKE 'BT%')
UNION ALL SELECT N'r00 (sổ cái) Q1',   (SELECT COUNT(*) FROM r00$202601 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM r00$202602 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM r00$202603 WHERE stt_rec LIKE 'BT%')
UNION ALL SELECT N'r70 (sổ kho) Q1',   (SELECT COUNT(*) FROM r70$202601 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM r70$202602 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM r70$202603 WHERE stt_rec LIKE 'BT%')
UNION ALL SELECT N'r90 (sổ kho TT) Q1',(SELECT COUNT(*) FROM r90$202601 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM r90$202602 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM r90$202603 WHERE stt_rec LIKE 'BT%')
UNION ALL SELECT N'r20 (sổ thuế) Q1',  (SELECT COUNT(*) FROM r20$202601 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM r20$202602 WHERE stt_rec LIKE 'BT%') + (SELECT COUNT(*) FROM r20$202603 WHERE stt_rec LIKE 'BT%')
GO
