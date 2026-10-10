--//// Bcode Report Builder /////// Created At: [#CREATEDAT#] /////////////////////////
[#DropIfExists#]IF OBJECT_ID('dbo.[#PROCNAME#]', 'P') IS NOT NULL DROP PROCEDURE [dbo].[[#PROCNAME#]]
GO
[#DropIfExists#]CREATE PROCEDURE [dbo].[[#PROCNAME#]]
[#PARAMS#]
AS
BEGIN
	SET NOCOUNT ON
	SET ANSI_NULLS OFF

[#BODY#]

	-- exec dbo.[#PROCNAME#] [#SAMPLE#]
	-- [[#PROCNAME#]]

	SET NOCOUNT OFF
	SET ANSI_NULLS ON
END
