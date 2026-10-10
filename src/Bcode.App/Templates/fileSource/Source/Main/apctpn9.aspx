<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Chứng từ phải trả khác, chứng từ bù trừ công nợ" e="Misc. Payable Transaction, AP-AR Interface"%>
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
        End Sub
    </script>
	
	<div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="JPTran"/>
	<FastBusiness:QueryExtender runat="server" TargetControlID="queryMainContainer" ID="JPTran" GridID="MainReport" Controller="JPTran" SinglePage="false" />
    <div id="queryInitGrid" style="display:none;">
        <asp:Panel ID="queryMainContainer" runat="server" CssClass="QueryMainContainer" />
    </div>  	  
</asp:Content>