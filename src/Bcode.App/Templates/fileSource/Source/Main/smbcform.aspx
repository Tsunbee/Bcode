<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page"
    v="Khai báo phân tích tổng hợp tình hình kinh doanh" e="Synthetic Analysis of Performance Definition" %>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server">
</asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div>
        <asp:Panel ID="panelReport" runat="server" />
    </div>
    <fastbusiness:reportextender id="MainReport" runat="server" targetcontrolid="panelReport"
        readonly="true" controller="BCForm000" />
</asp:Content>
