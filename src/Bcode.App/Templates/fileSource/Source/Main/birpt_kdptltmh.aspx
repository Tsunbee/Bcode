<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Phân tích lỗi kiểm định theo mặt hàng" e="Inspection Defect Analysis by Item"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0">
    </asp:Panel>
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" Controller="rptBIInspectionDefectAnalysisByItem" FilterMode = "true" />
</asp:Content>