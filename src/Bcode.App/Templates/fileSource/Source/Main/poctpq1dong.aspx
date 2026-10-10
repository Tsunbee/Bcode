<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Đóng đề nghị báo giá" e="RFQ Close" %>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
  <div>
    <asp:Panel ID="panelReport" runat="server" />
  </div>
  <fastbusiness:reportextender id="MainReport" runat="server" targetcontrolid="panelReport" controller="PQClose" />
</asp:Content>
