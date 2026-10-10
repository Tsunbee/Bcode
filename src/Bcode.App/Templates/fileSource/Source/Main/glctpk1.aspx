<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Phiếu kế toán tổng hợp" e="General Voucher"%>
<%@ Register Assembly="FastBusiness.QueryExtender" Namespace="FastBusiness.QueryExtender" TagPrefix="FastBusiness" %>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
		<asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0">
				<script type="text/javascript" src="../AppHandler/ExternalScript.ashx?t=PDF&m=1&v=1"></script>
		</asp:Panel>
		
		<asp:Panel ID="ExtenderPanel" runat="server" Width="0" Height="0"></asp:Panel>
		<script runat="server">
				Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
						ExtenderPanel.Controls.Add(LoadControl("TreeView.ascx"))
			Dim f As String = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Extender.txt"
						If IO.File.Exists(f) Then
								Dim v As String = IO.File.ReadAllText(f)
								If v = "INCLUDE" Then
										ResourcePanel.Controls.Add(LoadControl("ExtenderControls.ascx"))
								End If
						End If
			ExtenderPanel.Controls.Add(LoadControl("Chart.ascx"))
			f = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Multiviewer.txt"
						If IO.File.Exists(f) Then
								Dim v As String = IO.File.ReadAllText(f)
								If v = "INCLUDE" Then
										ResourcePanel.Controls.Add(LoadControl("MultiViewer.ascx"))
								End If
						End If
				End Sub
		</script>
	
		<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="GLTran"/> 
		<FastBusiness:QueryExtender runat="server" TargetControlID="queryMainContainer" ID="GLTran" GridID="MainReport" Controller="GLTran" SinglePage="false" />
		<div id="queryInitGrid" style="display:none;">
				<asp:Panel ID="queryMainContainer" runat="server" CssClass="QueryMainContainer" />
		</div>
		
		<div id="voucherContainer" style="width: 100%; height: 100%"></div>
		<div id="voucherMaster" style="width: 100%; height: 50%; display: none;">
				<asp:Panel ID="panelReport" runat="server" />
		</div>
		<div id="voucherDetail" style="width: 100%;">
				<asp:Panel ID="panelDetail" runat="server" />
		</div>

</asp:Content>