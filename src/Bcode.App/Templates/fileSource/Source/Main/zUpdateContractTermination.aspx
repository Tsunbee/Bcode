<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Cập nhật thanh lý hợp đồng" e="Update Contract Termination"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
<script src="../ClientScript/jAjax.js"></script>
		<div>
				<asp:Panel ID="panelReport" runat="server"/> 
		</div>
		<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" Controller="zUpdateContractTermination" FilterMode="true" InitScript="var g=this;
		g._resources.Pager.PageSizes=[50,100,150,200,250];
		g._gridPageSize=g._resources.Pager.PageSizes[1];"/>
</asp:Content> 