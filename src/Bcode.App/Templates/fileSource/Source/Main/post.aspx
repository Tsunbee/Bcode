<%@ Page Language="vb" AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Thông tin chung..." e="Post..."%>

<%@ Register Assembly="PostExtender" Namespace="PostExtender" TagPrefix="post" %>
<asp:Content ID="Content1" ContentPlaceHolderID="head" runat="server">
</asp:Content>
<asp:Content ID="Content2" ContentPlaceHolderID="FastBusiness" runat="server">
    <div>
        <asp:Panel ID="panelReport" runat="server" Style="display: none" />
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="Main" />
    <div id="ViewPostID">
        <asp:Panel ID="panelPost" runat="server" />
    </div>
    <post:PostExtenderControl ID="PostControl" runat="server" TargetControlID="panelPost" MainPage="true" Controller="Post01" ParentID="mpMainBody" ElementID="ViewPostID" PostMethod="GetViewPost" HandlerPath="../AppHandler/PostExtender.ashx" ServicePath="../AppService/PostExtender.asmx"/>

</asp:Content>
