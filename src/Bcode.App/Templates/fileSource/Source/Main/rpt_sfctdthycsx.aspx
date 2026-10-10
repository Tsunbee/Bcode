<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Bảng tiến độ thực hiện yêu cầu sản xuất" e="Daily Production Gantt Chart" %>

<%@ Register Assembly="FastBusiness.Gantt" Namespace="FastBusiness" TagPrefix="graph" %>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server">
</asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div>
        <asp:Panel ID="panelReport" runat="server" />
    </div>
    <fastbusiness:reportextender id="MainReport" runat="server" targetcontrolid="panelReport" readonly="true" controller="rptSFCDailyProductionGanttChart" filtermode="true" initscript="var g=this;g._resources.Pager.PageSizes=[50,100,150,200,250];g._gridPageSize=g._resources.Pager.PageSizes[4];" />
    <div style="display: none">
        <asp:Panel ID="panelGraph" runat="server" />
    </div>
    <graph:Gantt ID="graphMain" TargetControlID="panelGraph" runat="server" />
</asp:Content>