<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Đề nghị báo giá" e="Request for Quotation"%>
<%@ Register Assembly="Flow" Namespace="Flow" TagPrefix="flow" %>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
   <script src="../ClientScript/AES.js?v=2" type="text/javascript"></script>
    <script src="../ClientScript/CRC32.js" type="text/javascript"></script>
    <script src="../ClientScript/TextEditor.js" type="text/javascript"></script>
    <link rel="stylesheet" type="text/css" href="../Css/TextEditor.css"/>
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
	<div style="display:none;">
        <asp:Panel ID="panelFlow" runat="server" />
        <flow:ExtenderControl ID="Flow" runat="server" TargetControlID="panelFlow" Controller="AdvancedPurchasing" ServicePath="../AppService/FlowExtender.asmx" ServiceMethod="GetFlowViewPage" Resource="1"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="PQTran"/>
    
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
            f = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Chat.txt"
            If Not IO.File.Exists(f) OrElse (IO.File.Exists(f) AndAlso IO.File.ReadAllText(f) = "IGNORE") Then
                Page.ClientScript.RegisterClientScriptInclude("jquery", ResolveUrl("~/ClientScript/jquery.min.js"))
            End If
        End Sub
    </script>
    
</asp:Content>