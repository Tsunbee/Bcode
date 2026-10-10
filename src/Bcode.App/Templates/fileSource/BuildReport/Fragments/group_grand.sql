	IF EXISTS (SELECT 1 FROM #report WHERE sysorder = 5)
		INSERT INTO #report (sysorder, sysprint, systotal, [#COLS#], [#XGCOLS#])
			SELECT 7, 1, 0, [#SEL#], [#XGSEL#]
			FROM #report WHERE sysorder = 5