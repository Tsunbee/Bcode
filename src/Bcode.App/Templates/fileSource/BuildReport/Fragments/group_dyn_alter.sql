	-- Nhóm theo chọn lúc chạy: cột sắp xếp dùng chung cho mọi lựa chọn (Không nhóm thì xg1 để trống)
	ALTER TABLE #report ADD xid INT IDENTITY(1, 1) NOT NULL, [#XGCOLS#]