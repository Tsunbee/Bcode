<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Báo cáo tồn theo kho" e="Stock Report by Site" %>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server">
</asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0">
    </asp:Panel>
    <script runat="server">
        Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
            ResourcePanel.Controls.Add(LoadControl("Chart.ascx"))
        End Sub
    </script>
    <div>
        <asp:Panel ID="panelReport" runat="server" />
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="rptStockBySite" FilterMode="true" />
</asp:Content>
