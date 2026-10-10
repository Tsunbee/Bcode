<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Bảng tổng hợp chỉ tiêu hợp nhất dòng tiền và thuyết minh" e="Summary Report on the Consolidating Articles of Cash Flow Statement and Financial Interpretation"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
<div>
    <asp:Panel ID="panelReport" runat="server"/>
</div>
<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="rptCFSummaryArticleOfCashFlowAndFinancial" FilterMode="true"/>
</asp:Content>
