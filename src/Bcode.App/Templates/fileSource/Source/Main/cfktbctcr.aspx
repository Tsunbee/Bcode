<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Kiểm tra báo cáo tài chính riêng chưa gửi" e="Check Unsent Subsidiary Financial Reports"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <script src="../ClientScript/TextEditor.js" type="text/javascript"></script>
    <link rel="stylesheet" type="text/css" href="../Css/TextEditor.css"/>
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" FilterMode="true" ReadOnly="true" Controller="CFCheckUnsentSubsidiaryFinancialReports"/>
	<asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0"></asp:Panel>
	<script runat="server">
        Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
            Dim f As String = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Extender.txt"
            f = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Chat.txt"
            If Not IO.File.Exists(f) OrElse (IO.File.Exists(f) AndAlso IO.File.ReadAllText(f) = "IGNORE") Then
                Page.ClientScript.RegisterClientScriptInclude("jquery", ResolveUrl("~/ClientScript/jquery.min.js"))
            End If
        End Sub
    </script>
</asp:Content>