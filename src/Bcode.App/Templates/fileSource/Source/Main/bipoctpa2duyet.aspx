<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Duyệt hợp đồng mua hàng" e="Scheduling Agreement Approval"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<%@ Register Assembly="PostExtender" Namespace="PostExtender" TagPrefix="post" %>
<%@ Register Assembly="Flow" Namespace="Flow" TagPrefix="flow" %>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <script type="text/javascript" src="../AppHandler/ExternalScript.ashx?t=Calendar&m=1&v=1" charset="utf-8"></script>
    <link type="text/css" rel="stylesheet" href="../AppHandler/ExternalCss.ashx?t=Calendar&v=1" />
    <asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0"></asp:Panel>

    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" Controller="BISAApproval"/>
    <post:PostExtenderControl ID="PostControl" runat="server" TargetControlID="panelPost" />
    
	<div style="display:none;">
        <asp:Panel ID="panelFlow" runat="server" />
        <flow:ExtenderControl ID="Flow" runat="server" TargetControlID="panelFlow" Controller="AdvancedPurchasing" ServicePath="../AppService/FlowExtender.asmx" ServiceMethod="GetFlowViewPage" Resource="1"/>
    </div>
    <div id="approvalContainer"></div>
    <div id="approvalLeftPanel" style="width:100%;height:100%;"></div>
    <div id="approvalMaster" style="width:100%;display:none;">
        <asp:Panel ID="panelReport" runat="server" />
    </div>
    <div id="approvalDetail" style="width:100%;">
        <asp:Panel ID="panelDetail" runat="server" />
    </div>
     <div id="approvalAttachment" style="width:100%;">
        <asp:Panel ID="panelFiles" runat="server" />
    </div>
    <div id="approvalHistory" style="width:100%;font-family: Verdana;font-size: 11px; color: #444;">
    <div id="approvalPost" style="width:100%;font-family: Verdana;font-size: 11px; color: #444;display: none;background-color: #f3f7f8;height:100%;">
        <asp:Panel ID="panelPost" runat="server" />
    </div>	
</asp:Content>