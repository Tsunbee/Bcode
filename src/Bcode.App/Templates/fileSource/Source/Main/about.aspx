<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Thông tin sản phẩm" e="About" %>

<%@ Register Assembly="About" Namespace="About" TagPrefix="ab" %>
<asp:Content ID="Content1" ContentPlaceHolderID="head" runat="server">
</asp:Content>
<asp:Content ID="Content2" ContentPlaceHolderID="FastBusiness" runat="server">
  <asp:Panel ID="panelAbout" runat="server" />
  <ab:ExtenderControl ID="About" runat="server" TargetControlID="panelAbout" ServicePath="../AppService/AboutExtender.asmx" ServiceMethod="GetAboutViewPage" ImagePath="../Images/About.jpg" />
</asp:Content>
