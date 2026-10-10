<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Đóng thỏa thuận mua hàng" e="Outline Agreement Close" %>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
  <div>
    <asp:Panel ID="panelReport" runat="server" />
  </div>
  <fastbusiness:reportextender id="MainReport" runat="server" targetcontrolid="panelReport" controller="BIOAClose" />
</asp:Content>
