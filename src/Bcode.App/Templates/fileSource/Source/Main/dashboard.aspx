<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Tổng quan" e="Main Dashboard" %>

<%@ Register Assembly="Dashboard" Namespace="Dashboard" TagPrefix="Dashboard" %>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0">
    </asp:Panel>
    <script runat="server">
        Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
            ResourcePanel.Controls.Add(LoadControl("Chart.ascx"))
        End Sub
    </script>
	<div style="display:none">
        <asp:Panel ID="panelReport" runat="server"/>
   </div>
  <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="Dashboard"/>
  <div class="DashboardContainer" style="height: 100%; width:100%">
    <asp:Panel ID="panelDashboard" runat="server" />
    <Dashboard:ExtenderControl ID="Dashboard" runat="server" TargetControlID="panelDashboard" Controller="Dashboard" ServicePath="../AppService/Dashboard.asmx" ServiceMethod="GetDashboardViewPage" />
  </div>
</asp:Content>
