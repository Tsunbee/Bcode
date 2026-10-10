<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Tạo hạch toán hợp đồng" e="Auto generation of Contract Receivable"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
<script src="../ClientScript/jAjax.js"></script>
		<div>
				<asp:Panel ID="panelReport" runat="server"/> 
		</div>
		<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" Controller="zGenerateHD93" FilterMode="true" InitScript="var g=this;
		g._resources.Pager.PageSizes=[5,10,15,20,150];
		g._gridPageSize=g._resources.Pager.PageSizes[1];"/>
</asp:Content> 