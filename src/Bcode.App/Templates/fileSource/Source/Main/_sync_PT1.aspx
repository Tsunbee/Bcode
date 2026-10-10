<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Đồng bộ phiếu thu" e="Bank Credit Advice"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
		<div>
				<asp:Panel ID="panelReport" runat="server"/>
		</div>
		<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" FilterMode="true" Controller="_sync_PT1"/>
</asp:Content>