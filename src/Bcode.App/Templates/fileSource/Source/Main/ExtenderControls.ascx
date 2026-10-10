<%@ Control Language="vb" AutoEventWireup="false" CodeBehind="ExtenderControls.ascx.vb" Inherits="System.Web.UI.UserControl" %>
<%@ Register Assembly="FileUploadExtender" Namespace="FileUploadExtender" TagPrefix="upload" %>
<%@ Register Assembly="PostExtender" Namespace="PostExtender" TagPrefix="post" %>

<div style="display: none"><asp:Panel ID="panelList" runat="server" /></div>
<upload:UploadExtenderControl ID="ListControl" runat="server" TargetControlID="panelList" />
<div style="display: none"><asp:Panel ID="panelPost" runat="server" /></div>
<post:PostExtenderControl ID="PostControl" runat="server" TargetControlID="panelPost" />
