<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Vào số dư đầu kỳ các công trình, vụ việc, theo hạng mục xây dựng" e="Project/Job Opening Balance, by Construction Items"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
		<div>
				<asp:Panel ID="panelReport" runat="server"/>
		</div>
		<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="zcdvvtd3"/>
</asp:Content>