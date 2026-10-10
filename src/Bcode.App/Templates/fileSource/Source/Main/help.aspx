<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page"
    v="Hướng dẫn sử dụng" e="Help" %>

<%@ Import Namespace="FastBusiness.AjaxControlExtender.Session" %>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server">
    <asp:Panel ID="Panel1" runat="server" Height="100%" Width="100%" HorizontalAlign="Center">
    </asp:Panel>

    <script runat="server">
        Dim language As String
        Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
            language = GetSession(SessionName.language)
            Panel1.Controls.Clear()
            Dim usChart101 As New UserControl
            If language.ToUpper = "V" Then
                usChart101 = LoadControl("help-vi.ascx")
            Else
                usChart101 = LoadControl("help-en.ascx")
            End If
            Panel1.Controls.Add(usChart101)
        End Sub
    </script>

</asp:Content>
<asp:Content ID="Content2" ContentPlaceHolderID="FastBusiness" runat="server">
</asp:Content>
