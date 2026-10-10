<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Báo cáo dư nợ" e="Balance Sheet"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
		<div>
				<asp:Panel ID="panelReport" runat="server"/>
		</div>
		<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport"  FilterMode="true" ReadOnly="true" Controller="zrpt_O023"/>

		<script runat="server">
				Private Sub Page_PreLoad(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.PreLoad           
						Dim f As String = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Circular.133"
						If IO.File.Exists(f) Then
								Dim v As String = IO.File.ReadAllText(f)
								If v = "INCLUDE" Then
										If FastBusiness.AjaxControlExtender.Session.GetSession(FastBusiness.AjaxControlExtender.Session.SessionName.language).ToLower = "v" Then
												CType(Page, FastBusiness.ReportExtender.UI.Page).v = "Báo cáo tình hình tài chính"
										Else
												CType(Page, FastBusiness.ReportExtender.UI.Page).e = "Financial Analysis Report"
										End If
								End If
						End If
				End Sub
		</script>
</asp:Content>