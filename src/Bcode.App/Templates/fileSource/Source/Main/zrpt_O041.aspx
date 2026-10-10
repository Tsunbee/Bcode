<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Báo cáo dư nợ & lãi suất - doanh nghiệp" e="Outstanding Balance & Interest Rate Report – Corporate"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
	<div>
			<asp:Panel ID="panelReport" runat="server"/>
	</div>
	<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="zrpt_O041" FilterMode="true"/>		
	 
</asp:Content>