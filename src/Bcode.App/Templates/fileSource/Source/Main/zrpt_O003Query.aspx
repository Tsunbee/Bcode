<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Chi tiết" e="???"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
	<div>
			<asp:Panel ID="panelReport" runat="server"/>
	</div>
	<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" [#REPORTTAG#]ReadOnly="true" [#REPORTTAG#]Controller="zrpt_O003Query" FilterMode="true"/>		
	 
</asp:Content>