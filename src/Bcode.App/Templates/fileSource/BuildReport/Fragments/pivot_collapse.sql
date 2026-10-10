	-- Gộp theo chiều ngang đã chọn (cột của các lựa chọn "Xoay theo" còn lại không tham gia): giữ một dòng cho mỗi ô hàng × cột ngang, cộng số liệu vào dòng đó
	ALTER TABLE #report ADD xid INT IDENTITY(1, 1) NOT NULL
	UPDATE r SET [#SETS#]
		FROM #report r JOIN (SELECT xRow, xColumn, MIN(xid) AS keep, [#SUMS#] FROM #report GROUP BY xRow, xColumn) x ON x.xRow = r.xRow AND x.xColumn = r.xColumn AND r.xid = x.keep
	DELETE r FROM #report r JOIN (SELECT xRow, xColumn, MIN(xid) AS keep FROM #report GROUP BY xRow, xColumn) x ON x.xRow = r.xRow AND x.xColumn = r.xColumn WHERE r.xid <> x.keep
	ALTER TABLE #report DROP COLUMN xid