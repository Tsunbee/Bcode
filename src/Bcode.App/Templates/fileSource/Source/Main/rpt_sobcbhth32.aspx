<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Báo cáo bán hàng nhóm theo 3 chỉ tiêu" e="Sales Summary Group by Three Criteria"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
<div>
<asp:Panel ID="panelReport" runat="server"/>
</div>
<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="rptSalesSummaryByThreeCriteria2" FilterMode="true"/>

</asp:Content>
