	-- Dòng nhóm chỉ điền một số cột, các cột còn lại để NULL: SELECT INTO có thể đã tạo cột NOT NULL nên cho phép NULL hết
	DECLARE [#VAR#] NVARCHAR(MAX) = N''
	SELECT [#VAR#] = [#VAR#] + N'ALTER TABLE [#TBL#] ALTER COLUMN [' + c.name + N'] ' + t.name
		+ CASE WHEN t.name IN ('char', 'varchar', 'binary', 'varbinary') THEN N'(' + CASE WHEN c.max_length = -1 THEN N'MAX' ELSE CAST(c.max_length AS NVARCHAR(10)) END + N')'
			WHEN t.name IN ('nchar', 'nvarchar') THEN N'(' + CASE WHEN c.max_length = -1 THEN N'MAX' ELSE CAST(c.max_length / 2 AS NVARCHAR(10)) END + N')'
			WHEN t.name IN ('decimal', 'numeric') THEN N'(' + CAST(c.precision AS NVARCHAR(10)) + N', ' + CAST(c.scale AS NVARCHAR(10)) + N')'
			ELSE N'' END + N' NULL; '
		FROM tempdb.sys.columns c JOIN tempdb.sys.types t ON t.user_type_id = c.user_type_id
		WHERE c.object_id = OBJECT_ID('tempdb..[#TBL#]') AND c.is_nullable = 0 AND c.is_identity = 0 AND c.is_computed = 0
	IF [#VAR#] <> N'' EXEC ([#VAR#])
