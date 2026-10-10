<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Phân tích bán hàng nhiều kỳ theo 2 chỉ tiêu" e="Multi-period Sales Analysis by Two Criteria" %>

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
    <fastbusiness:reportextender id="MainReport" runat="server" targetcontrolid="panelReport" readonly="true" controller="rptMultiPeriodSalesAnalysisByTwoCriteria2" filtermode="true" />
</asp:Content>