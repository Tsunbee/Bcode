<%@ Page Language="VB" AutoEventWireup="false" Inherits="FastBusiness.PasswordRecoveryExtender.ResetPassword" LoginPage="Main/Login.aspx" %>

<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Transitional//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd">
<html xmlns="http://www.w3.org/1999/xhtml">
<head id="mainHead" runat="server">
    <title>Fast Business Online</title>
    <meta http-equiv="X-UA-Compatible" content="IE=8;IE=edge;"/>
    <link href="~/Images/favicon.ico" rel="shortcut icon" type="image/ico"/>
    <style type="text/css">
        .ResetPasswordBody{background:none repeat scroll 0 0 #edede2;border:0 none;font-family:'Microsoft Sans Serif';font-size:11px;margin:0}
        .ResetPasswordBar{position:relative;background-color:#fff;border-bottom:1px solid silver;border-top:0 solid silver;height:25px;padding:0;width:100%}
        .ProcessCompleted{color:#444}
    </style>
</head>

<body class="ResetPasswordBody">
    <div class="ResetPasswordBar">
    </div>
    <div style="margin-top: 8px; margin-left: 16px">
        <asp:Label ID="lblMessage" CssClass="ProcessCompleted" runat="server"></asp:Label>
    </div>
</body>

</html>