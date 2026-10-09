<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="[#TITLE#]" e="[#TITLE2#]"%>
[#Code4Comment#]<%@ Register Assembly="PostExtender" Namespace="PostExtender" TagPrefix="post" %>[#Code4Comment#]

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
	<div>
			<asp:Panel ID="panelReport" runat="server"/>
	</div>
	<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="[#CONTROLLER#]"/>		
	[#Code4Comment#]
	<div style="display: none"><asp:Panel ID="panelPost" runat="server" /></div>
	<post:PostExtenderControl ID="PostControl" runat="server" TargetControlID="panelPost" />
	[#Code4Comment#]
</asp:Content>