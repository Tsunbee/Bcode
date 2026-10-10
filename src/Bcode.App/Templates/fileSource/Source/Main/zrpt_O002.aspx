<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Báo cáo công nợ phải trả ngân hàng/Đối tác và Khách hàng góp vốn" e="Bank,Partner,Customer payable report"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
<script src="https://cdnjs.cloudflare.com/ajax/libs/moment.js/2.30.1/moment.min.js" integrity="sha512-QoJS4DOhdmG8kbbHkxmB/rtPdN62cGWXAdAFWWJPvUFF1/zxcPSdAnn4HhYZSIlVoLVEJ0LesfNlusgm2bPfnA==" crossorigin="anonymous" referrerpolicy="no-referrer"></script>
	<div>
			<asp:Panel ID="panelReport" runat="server"/>
	</div>
	<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="zrpt_O002" FilterMode="true"/>		
	 
</asp:Content>