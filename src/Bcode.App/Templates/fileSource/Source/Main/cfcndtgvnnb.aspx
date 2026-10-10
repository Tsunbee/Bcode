<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Cập nhật doanh thu, giá vốn cho phiếu nhập nội bộ" e="Update Revenue and Cost of Goods Sold for the Intercompany Purchase Transactions"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" FilterMode="true" ReadOnly="true"  Controller="CFUpdateRevenueAndCostOfGoodsSold"/>
</asp:Content>