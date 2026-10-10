	-- Nhóm báo cáo ([#LEVELS#] cấp): dòng tiêu đề nhóm (sysorder = 4), dòng cộng nhóm (sysorder = 6)
[#Alter#]	ALTER TABLE #report ADD xid INT IDENTITY(1, 1) NOT NULL, [#XGCOLS#]
[#Alter#]	;WITH w AS (SELECT xid, [#RANKS#] FROM #report)
		UPDATE #report SET [#RANKSET#] FROM #report r JOIN w ON w.xid = r.xid
[#Stt#]	;WITH w AS (SELECT xid, ROW_NUMBER() OVER (ORDER BY [#XGLIST#], stt) AS n FROM #report)
		UPDATE #report SET stt = w.n FROM #report r JOIN w ON w.xid = r.xid
[#Stt#]