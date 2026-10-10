<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Bảng tính chi phí CCDC nhiều kỳ" e="Multi Period Tool & Supply Allocation Report" %>

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
    <fastbusiness:reportextender id="MainReport" runat="server" targetcontrolid="panelReport" readonly="true" controller="rptMultiPeriodTS2Allocation" filtermode="true" />
</asp:Content>