<%@ Page Language="VB" AutoEventWireup="false" Inherits="Message.Approve" %>

<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Transitional//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd">
<html xmlns="http://www.w3.org/1999/xhtml">
<head id="mainHead" runat="server">
    <title>Fast Business Online</title>
    <meta name="viewport" content="width = device-width, initial-scale = 1, maximum-scale = 1, minimum-scale = 1, user-scalable = no, minimal-ui" />
    <meta name="apple-mobile-web-app-capable" content="yes" />
    <meta name="apple-mobile-web-app-status-bar-style" content="black" />
    <meta http-equiv="X-UA-Compatible" content="IE=8;IE=edge;" />
    
    <link href="~/Images/favicon.ico" rel="shortcut icon" type="image/ico" />
    <link href="~/Css/Comment.css" rel="stylesheet" type="text/css" />
    <script type="text/javascript">
        function b64encode(str) {return window.btoa(encodeURIComponent(str).replace(/%([0-9A-F]{2})/g, function toSolidBytes(match, p1) {return String.fromCharCode('0x' + p1);}));}
    </script>
</head>
<body class="MenuBody">
    <div class="HeaderBar"></div>
    <div class="MenuExtenderBar"></div>
    <div style="padding-top:8px;">
        <asp:Label ID="lblMessage" CssClass="ProcessCompleted" runat="server"></asp:Label>
    </div>
    <asp:Literal ID="literalContent" runat="server" Visible="false"></asp:Literal>
</body>
</html>
