<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Tổng hợp doanh thu bán hàng theo bộ phận" e="Sales Summary by Department"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="rptSalesSummaryByDepartment" FilterMode="true" />
</asp:Content>