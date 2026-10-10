<%@ Page Language="vb" AutoEventWireup="false" Inherits="FastBusiness.PasswordRecoveryExtender.PasswordRecovery" %>
<%@ Register Assembly="FastBusiness.PasswordRecoveryExtender" Namespace="FastBusiness.PasswordRecoveryExtender" TagPrefix="cc1" %>

<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Transitional//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd">

<html xmlns="http://www.w3.org/1999/xhtml">
<head runat="server">
    <title>Fast Business Online</title>
    <meta http-equiv="X-UA-Compatible" content="IE=8;IE=edge;"/>
    <link href="~/Images/favicon.ico" rel="shortcut icon" type="image/ico"/>
    <link href="~/Css/Control.css" rel="stylesheet" type="text/css" />
</head>

<body style="margin:0;padding:0;font-size:11px;">
    <form id="PasswordRecoveryForm" runat="server">
       <asp:ScriptManager ID="LoginScriptManager" runat="server" EnablePageMethods="true" ScriptMode="Release" LoadScriptsBeforeUI="false" EnablePartialRendering="false">
            <CompositeScript ScriptMode="Release" Path="~/ClientScript/j8.js">
                <Scripts>
                    <asp:ScriptReference name="MicrosoftAjax.js"  />
                </Scripts>
            </CompositeScript>
        </asp:ScriptManager  >
        
        <asp:Panel ID="PasswordRecoveryPanel" runat="server" />
        <cc1:PasswordRecoveryExtender ID="PasswordRecoveryExtender" runat="server" TargetControlID="PasswordRecoveryPanel"/>
    </form>
</body>
</html>