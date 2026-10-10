<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Đơn hàng bán" e="Sales Order" %>
<%@ Register Assembly="FastBusiness.QueryExtender" Namespace="FastBusiness.QueryExtender" TagPrefix="FastBusiness" %>
<%@ Register Assembly="Flow" Namespace="Flow" TagPrefix="flow" %>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <script src="../ClientScript/AES.js?v=2" type="text/javascript"></script>
    <script src="../ClientScript/CRC32.js" type="text/javascript"></script>
    <script src="../ClientScript/TextEditor.js" type="text/javascript"></script>
    <link rel="stylesheet" type="text/css" href="../Css/TextEditor.css"/>
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
            f = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Chat.txt"
            If Not IO.File.Exists(f) OrElse (IO.File.Exists(f) AndAlso IO.File.ReadAllText(f) = "IGNORE") Then
                Page.ClientScript.RegisterClientScriptInclude("jquery", ResolveUrl("~/ClientScript/jquery.min.js"))
            End If
            ExtenderPanel.Controls.Add(LoadControl("Chart.ascx"))
        End Sub
    </script>
	<div style="display:none;">
        <asp:Panel ID="panelFlow" runat="server" />
        <flow:ExtenderControl ID="Flow" runat="server" TargetControlID="panelFlow" Controller="AdvancedPurchasing" ServicePath="../AppService/FlowExtender.asmx" ServiceMethod="GetFlowViewPage" Resource="1"/>
    </div>
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
	  <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="SOTran"/>
	  <FastBusiness:QueryExtender runat="server" TargetControlID="queryMainContainer" ID="SOTran" GridID="MainReport" Controller="SOTran" SinglePage="false" />
	  <div style="display: none;" id="queryInitGrid">
		 <asp:Panel ID="queryMainContainer" runat="server" CssClass="QueryMainContainer" />
    </div>
	
	<div style="display: none;" id="subReport">
</asp:Content>
