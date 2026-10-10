<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Phân tích cơ cấu tài chính kỳ" e="Financial Structure Analysis"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
<div>
<asp:Panel ID="panelReport" runat="server"/>
</div>
<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport"  FilterMode="true" Controller="rptFinancialStructureAnalysis"/>
</asp:Content>
