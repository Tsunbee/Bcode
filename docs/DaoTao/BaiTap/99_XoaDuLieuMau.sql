/* =====================================================================================
   BÀI TẬP ĐÀO TẠO — 99. DỌN DATABASE LUYỆN TẬP
   Xoá dữ liệu mẫu (danh mục user_id0 = -1; chứng từ và mọi sổ đã post có stt_rec 'BT%'; tồn hiện thời của vật tư mẫu)
   và các object tạo khi tập:
   procedure / bảng tên zbt_..., bảng sao lưu zbk_dmkh_bt..., bảng zdmhtbh (Buổi 5).
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
BEGIN TRAN

DECLARE @t sysname, @q nvarchar(max)
-- Mọi bảng có cột stt_rec: chứng từ (m/d), sổ chứng từ (c81…), tra cứu (i81…), sổ cái r00, sổ kho r70 / r90, sổ thuế r20,
-- bảng trung gian ct00 / ct70 / ct90 / ctgt20 / cttt20, ctgt21, hoá đơn điện tử hddt00 / hddt01, p00…
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

-- Tồn hiện thời (cộng dồn khi post) của vật tư mẫu
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

-- Object tạo khi tập
DECLARE d CURSOR LOCAL STATIC FOR
    SELECT CASE type WHEN 'P' THEN 'DROP PROCEDURE ' WHEN 'U' THEN 'DROP TABLE ' WHEN 'V' THEN 'DROP VIEW ' ELSE 'DROP FUNCTION ' END + QUOTENAME(name)
    FROM sys.objects
    WHERE type IN ('P', 'U', 'V', 'FN', 'TF', 'IF')
      AND (name LIKE 'zbt[_]%' OR name LIKE 'zbk[_]dmkh[_]bt%' OR name = 'zdmhtbh')
OPEN d
FETCH NEXT FROM d INTO @q
WHILE @@FETCH_STATUS = 0
BEGIN
    PRINT @q
    EXEC (@q)
    FETCH NEXT FROM d INTO @q
END
CLOSE d
DEALLOCATE d

COMMIT
GO
SET NOEXEC OFF
GO
SELECT N'Còn lại' AS kiem_tra,
       (SELECT COUNT(*) FROM dmkh WHERE user_id0 = -1) AS dmkh_mau,
       (SELECT COUNT(*) FROM sys.objects WHERE name LIKE 'zbt[_]%' OR name = 'zdmhtbh') AS object_tap
GO
