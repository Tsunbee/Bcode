<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Cập nhật báo giá" e="Quotation"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="BIPQTran"/>
    
    <asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0"></asp:Panel>
    <asp:HiddenField ID="HiddenFieldSSID" runat="server" />
    <script runat="server">
        Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
            Dim f As String = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Extender.txt"
            If IO.File.Exists(f) Then
                Dim v As String = IO.File.ReadAllText(f)
                If v = "INCLUDE" Then
                    ResourcePanel.Controls.Add(LoadControl("ExtenderControls.ascx"))
                End If
            End If
			Dim algorithm As System.Security.Cryptography.SHA256 = System.Security.Cryptography.SHA256.Create()
			Dim u1 As String = FastBusiness.AjaxControlExtender.Session.GetSession(FastBusiness.AjaxControlExtender.Session.SessionName.userID)
			Dim u2 As String = FastBusiness.AjaxControlExtender.Session.GetSession(FastBusiness.AjaxControlExtender.Session.SessionName.unit)
			Dim defaultKey As String = Session.SessionID + "$" + u1 + "$" + u2
			Dim hash As Byte() = (algorithm.ComputeHash(Encoding.UTF8.GetBytes(defaultKey)))
			Dim ssID = Convert.ToBase64String(hash)
			HiddenFieldSSID.Value = ssID
        End Sub
    </script>
    
</asp:Content>