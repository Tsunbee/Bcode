<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Phân bổ trả tiền hàng tự động cho các hóa đơn" e="Advanced Payment Allocation to Invoices" %>

<%@ Register Assembly="AllocationExtender" Namespace="AllocationExtender" TagPrefix="al" %>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>

<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div id="hiddentGrid" style="display: none;"><asp:Panel ID="panelReport" runat="server" /></div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" FilterMode="true" Controller="APAllocation" />
    <div id="AllocationContainer" class="AllocationContainer"></div>
    <asp:Panel ID="panelAllocation" runat="server" />
    <al:AllocationControl ID="allocationControl" runat="server" TargetControlID="panelAllocation" />
</asp:Content>
