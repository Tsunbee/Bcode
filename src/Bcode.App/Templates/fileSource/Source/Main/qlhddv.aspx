<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Danh sách hóa đơn đầu vào" e="Input Invoice List"%>
<%@ Register Assembly="FileUploadExtender" Namespace="FileUploadExtender" TagPrefix="upload" %>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
	<script src="../ClientScript/AES.js?v=2" type="text/javascript"></script>
		<script type="text/javascript" src="../AppHandler/ExternalScript.ashx?t=Calendar&m=1&v=1" charset="utf-8"></script>
		<link type="text/css" rel="stylesheet" href="../AppHandler/ExternalCss.ashx?t=Calendar&v=1" />

		<div id="MultiView.InputInvoice.Container"></div>
		<div id="MultiView.InputInvoice.Master" style="width:100%;display:none;">
				<asp:Panel ID="panelReport" runat="server" />
		</div>
	<div id="MultiView.InputInvoice.LeftPanel" style="width:100%;height:100%;"></div>
	<div id="MultiView.InputInvoice.Detail" style="width:100%;">
				<asp:Panel ID="panelDetail" runat="server" />
		</div>
		<div id="MultiView.InputInvoice.RightPanel" style="width:100%;height:100%;"></div>
	<div id="MultiView.InputInvoice.Seller" class="GridViewDiv"></div>
	<div id="MultiView.InputInvoice.Buyer" class="GridViewDiv"></div>
	<div id="MultiView.InputInvoice.DigitalSignature" class="GridViewDiv"></div>
	<div id="MultiView.InputInvoice.Verification" class="GridViewDiv"></div>

		<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="InputInvoice" />
		<FastBusiness:ReportExtender ID="CustomerReport" runat="server" TargetControlID="subCustomer" ReadOnly="true" Controller="Customer" InitScript="document._customerView = true;document._customerGrid = this;var a = []; Array.add(a, { Name: 'ma_kh', Opr: '=', Value: '', Type: 'String', Ignore: false }); this.set_externalKey(a);" />
		
		<div style="display: none;">
				<div id="MultiView.Container"></div>
				<div id="MultiView.Master" style="width: 100%; display: none;">
						<asp:Panel ID="subCustomer" runat="server" />
				</div>
				<div id="MultiView.RightPanel" style="width: 100%; height: 100%;"></div>
				<div id="MultiView.General" class="GridViewDiv"></div>
				<div id="MultiView.Payment" class="GridViewDiv"></div>
		</div>
		

		<div style="display: none;" id="subReport">
		<div style="display: none"><asp:Panel ID="panelList" runat="server" /></div>
		<upload:UploadExtenderControl ID="ListControl" runat="server" TargetControlID="panelList" />

		<asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0"></asp:Panel>
		<script runat="server">
				Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
						Dim f As String = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Extender.txt"
						If IO.File.Exists(f) Then
								Dim v As String = IO.File.ReadAllText(f)
								If v = "INCLUDE" Then
										ResourcePanel.Controls.Add(LoadControl("ExtenderControls.ascx"))
								End If
						End If
				End Sub
		</script>
</asp:Content>