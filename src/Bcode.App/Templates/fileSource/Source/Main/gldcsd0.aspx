<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Điều chỉnh số dư đầu kỳ các tài khoản" e="Account Opening Balance Adjustment"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" FilterMode="true" Controller="AccountBalanceAdjustment"/>
    <script runat="server">
        Private Sub Page_PreLoad(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.PreLoad           
            Dim p As String = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Tiny.External.txt"
			If IO.File.Exists(p) Then
                If IO.File.ReadAllText(p) = "INCLUDE" Then
                    If FastBusiness.AjaxControlExtender.Session.GetSession(FastBusiness.AjaxControlExtender.Session.SessionName.language).ToLower = "v" Then
                        CType(Page, FastBusiness.ReportExtender.UI.Page).v = "Điều chỉnh số dư đầu năm các tài khoản"
                    Else
                        CType(Page, FastBusiness.ReportExtender.UI.Page).e = "Account Opening Balance Adjustment"
                    End If
                End If
            End If
        End Sub
    </script>
</asp:Content>