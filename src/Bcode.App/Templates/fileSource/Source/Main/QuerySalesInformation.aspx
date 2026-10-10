<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Phân tích số liệu bán hàng" e="Sales Data Analysis" %>
<%@ Register Assembly="FastBusiness.QueryExtender" Namespace="FastBusiness.QueryExtender" TagPrefix="FastBusiness" %>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server">
    <asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0">
    </asp:Panel>
    <script runat="server">
        Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
            ResourcePanel.Controls.Add(LoadControl("Chart.ascx"))
        End Sub
    </script>
    <script type="text/javascript" src="../AppHandler/ExternalScript.ashx?t=PDF&m=1&v=1"></script>
    
</asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
  <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="initGrid" ReadOnly="true" Controller="SalesInformation" />
  <FastBusiness:QueryExtender runat="server" TargetControlID="queryMainContainer" ID="SalesInformation" GridID="MainReport" Controller="SalesInformation" />
  <div id="queryInitGrid" style="display: none;">
    <asp:Panel ID="initGrid" runat="server" />
  </div>
  <asp:Panel ID="queryMainContainer" runat="server" CssClass="QueryMainContainer" />
</asp:Content>
