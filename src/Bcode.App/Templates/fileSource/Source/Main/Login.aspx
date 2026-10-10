<%@ Page Language="vb" AutoEventWireup="false" Inherits="FastBusiness.LoginExtender.Login" %>
<%@ Register Assembly="AjaxControlToolkit" Namespace="AjaxControlToolkit" TagPrefix="cc1" %>
<%@ Register Assembly="FastBusiness.LoginExtender" Namespace="FastBusiness.LoginExtender" TagPrefix="cc2" %>

<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Transitional//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd">

<html xmlns="http://www.w3.org/1999/xhtml">
<head runat="server">
    <title>Fast Business Online</title>
	<meta http-equiv="X-UA-Compatible" content="IE=8;IE=edge;"/>
	<link href="~/Images/favicon.ico" rel="shortcut icon" type="image/ico"/>
	<link href="~/Css/Control.css" rel="stylesheet" type="text/css" />
	
	<asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0">
    </asp:Panel>
	<script runat="server">
	    Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
            Dim f As String = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\TFA.txt"
            If IO.File.Exists(f) Then
                Dim v As String = IO.File.ReadAllText(f)
                If v = "INCLUDE" Then
                    ResourcePanel.Controls.Add(LoadControl("TFA.ascx"))
                End If
            End If
        End Sub
    </script>
</head>

<body style="margin:0;padding:0;font-size:11px;">
    <form id="LoginForm" runat="server">
        <asp:ScriptManager ID="LoginScriptManager" runat="server" EnablePageMethods="true" ScriptMode="Release" LoadScriptsBeforeUI="false" EnablePartialRendering="false">
            <CompositeScript ScriptMode="Release" Path="~/ClientScript/j8.js">
                <Scripts>
                    <asp:ScriptReference name="MicrosoftAjax.js"  />
                    <asp:ScriptReference name="AjaxControlToolkit.ExtenderBase.BaseScripts.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.Common.Common.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.Compat.Timer.Timer.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.Compat.DragDrop.DragDropScripts.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.Animation.Animations.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.Animation.AnimationBehavior.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.HoverExtender.HoverBehavior.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.PopupExtender.PopupBehavior.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.DynamicPopulate.DynamicPopulateBehavior.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.DropDown.DropDownBehavior.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.DropShadow.DropShadowBehavior.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.DragPanel.FloatingBehavior.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.PopupControl.PopupControlBehavior.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.ModalPopup.ModalPopupBehavior.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.RoundedCorners.RoundedCornersBehavior.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="AjaxControlToolkit.AlwaysVisibleControl.AlwaysVisibleControlBehavior.js" Assembly="AjaxControlToolkit" />
                </Scripts>
            </CompositeScript>
        </asp:ScriptManager  >
        
        <asp:ScriptManagerProxy ID="ScriptProxyLoginPage" runat="server">
            <CompositeScript ScriptMode="Release" >
                <Scripts>
                    <asp:ScriptReference name="AjaxControlToolkit.NoBot.NoBotBehavior.js" Assembly="AjaxControlToolkit" />
                    <asp:ScriptReference name="FastBusiness.LoginExtender.LoginExtender.js" Assembly="FastBusiness.LoginExtender" />
                </Scripts>
            </CompositeScript>
        </asp:ScriptManagerProxy>
        <cc1:NoBot ID="PageNoBot" runat="server" OnGenerateChallengeAndResponse="PageGenerateChallengeAndResponse" ResponseMinimumDelaySeconds="3" CutoffMaximumInstances="30" CutoffWindowSeconds="60"/>
        <div style="display:none;"><input type="password"/></div>
        <asp:Panel ID="LoginPanel" runat="server" />
        <cc2:LoginExtender ID="LoginExtender" runat="server" TargetControlID="LoginPanel" Url="../Default.aspx" Incompatible = "../incompatible.htm" InformationUrl="~/Main/ChangePassword.aspx" ResetPasswordUrl="~/Main/PasswordRecovery.aspx"/>
    </form>
</body>
</html>
