<%@ Page Language="vb" AutoEventWireup="false" Inherits="FastBusiness.ChangePassword.UIPage" LoginUrl="~/Main/Login.aspx" %>

<%@ Register Assembly="FastBusiness.ChangePassword" Namespace="FastBusiness.ChangePassword" TagPrefix="cc1" %>

<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Transitional//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd">

<html xmlns="http://www.w3.org/1999/xhtml">
<head runat="server">
    <title>Fast Business Online</title>
    <meta charset="utf-8" />
    <meta http-equiv="X-UA-Compatible" content="IE=edge;" />
    <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, minimum-scale=1, user-scalable=yes, minimal-ui, viewport-fit=cover" />
    <meta name="apple-mobile-web-app-capable" content="yes" />
    <meta name="apple-mobile-web-app-status-bar-style" content="black" />
    <link href="~/Images/favicon.ico" rel="shortcut icon" type="image/ico" />

    <script src="../ClientScript/AES.js" type="text/javascript"></script>
    <script src="../ClientScript/CRC32.js" type="text/javascript"></script>
</head>

<body style="margin: 0; padding: 0; font-size: 11px;">
    <form id="PasswordRecoveryForm" runat="server">
        <asp:ScriptManager ID="LoginScriptManager" runat="server" EnablePageMethods="true" ScriptMode="Release" LoadScriptsBeforeUI="false" EnablePartialRendering="false">
            <CompositeScript ScriptMode="Release" Path="~/ClientScript/j8.js">
                <Scripts>
                    <asp:ScriptReference Name="MicrosoftAjax.js" />
                </Scripts>
            </CompositeScript>
        </asp:ScriptManager>

        <asp:Panel ID="ChangePasswordPanel" runat="server" />
        <cc1:ChangePassword ID="ChangePasswordExtender" runat="server" TargetControlID="ChangePasswordPanel" ServiceMethod="ChangePassword" ServicePath="../AppService/ChangePasswordService.asmx" Url="../Default.aspx" Height="75"/>
    </form>
</body>
</html>
