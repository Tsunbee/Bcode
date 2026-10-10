<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Khai báo nguồn cung ứng" e="Source List"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0">
    </asp:Panel>
    <asp:Panel ID="ExtenderPanel" runat="server" Width="0" Height="0"></asp:Panel>
    <script runat="server">
        Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
            ResourcePanel.Controls.Add(LoadControl("TreeView.ascx"))
            Dim f As String = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Extender.txt"
            If IO.File.Exists(f) Then
                Dim v As String = IO.File.ReadAllText(f)
                If v = "INCLUDE" Then
                    ExtenderPanel.Controls.Add(LoadControl("ExtenderControls.ascx"))
                End If
            End If
        End Sub
    </script>
    
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="BISLTran"/>
    
</asp:Content>