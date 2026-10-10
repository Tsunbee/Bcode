<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Kết chuyển số dư theo mã phí sang năm sau" e="Transfer Expense Balance to Next Year"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
		<div>
				<asp:Panel ID="panelReport" runat="server"/>
		</div>
		<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="ExpenseBalanceTransfer"/>
</asp:Content>