<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Khai báo các bút toán phân bổ tự động" e="Allocation Transaction Definition"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
		<div>
				<asp:Panel ID="panelReport" runat="server"/>
		</div>
		<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="AllocationTran" InitScript="var g=this;
		g._resources.Pager.PageSizes=[100,200,300,400,500];
		g._gridPageSize=g._resources.Pager.PageSizes[1];"/>
</asp:Content>