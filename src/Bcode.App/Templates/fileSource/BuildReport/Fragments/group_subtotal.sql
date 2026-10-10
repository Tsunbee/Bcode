	INSERT INTO #report (sysorder, sysprint, systotal, [#COLS#], [#XGCOLS#])
		SELECT 6, 1, 0, [#SEL#], [#XGSEL#]
		FROM #report WHERE sysorder = 5
		GROUP BY [#GROUPBY#]