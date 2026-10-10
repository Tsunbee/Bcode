<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Phiếu chi tiền mặt" e="Cash Disbursement"%>
<%@ Register Assembly="FastBusiness.QueryExtender" Namespace="FastBusiness.QueryExtender" TagPrefix="FastBusiness" %>
<%@ Register Assembly="Flow" Namespace="Flow" TagPrefix="flow" %>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <script src="../ClientScript/AES.js?v=2" type="text/javascript"></script>
    <script src="../ClientScript/CRC32.js" type="text/javascript"></script>
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
        End Sub
    </script>
    
	<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="CDTran"/>
	<FastBusiness:QueryExtender runat="server" TargetControlID="queryMainContainer" ID="CDTran" GridID="MainReport" Controller="CDTran" SinglePage="false" />
	<div style="display: none;" id="queryInitGrid">
	<asp:Panel ID="queryMainContainer" runat="server" CssClass="QueryMainContainer" />
    </div>
	<div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
	<div style="display:none;">
        <asp:Panel ID="panelFlow" runat="server" />
        <flow:ExtenderControl ID="Flow" runat="server" TargetControlID="panelFlow" Controller="AdvancedPurchasing" ServicePath="../AppService/FlowExtender.asmx" ServiceMethod="GetFlowViewPage" Resource="1"/>
    </div>
</asp:Content>