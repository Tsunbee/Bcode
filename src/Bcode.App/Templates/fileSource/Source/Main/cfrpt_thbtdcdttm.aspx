<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Bảng tổng hợp bút toán điều chỉnh dòng tiền và thuyết minh" e="Summary Report on Adjustment Entry for the Cash Flow Statement and Interpretation of Financial"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
<div>
    <asp:Panel ID="panelReport" runat="server"/>
</div>
<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="rptCFSummaryAdjustmentEntryOfCashFlowAndFinancial" FilterMode="true"/>
</asp:Content>
