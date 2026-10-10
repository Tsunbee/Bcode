<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Báo cáo số lượng chưa thanh toán từng dự án (C)" e="Report on the outstanding amount for each project (C)"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
	<div>
			<asp:Panel ID="panelReport" runat="server"/>
	</div>
	<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="zrpt_O053" FilterMode="true"/>		
	 
</asp:Content>