<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Quy trình hoạch định công suất" e="CRP Process Flowchart" %>

<%@ Register Assembly="Flow" Namespace="Flow" TagPrefix="flow" %>
<asp:Content ID="Content1" ContentPlaceHolderID="head" runat="server">
</asp:Content>
<asp:Content ID="Content2" ContentPlaceHolderID="FastBusiness" runat="server">
  <div class="FlowContainer">
    <asp:Panel ID="panelFlow" runat="server" />
    <flow:ExtenderControl ID="Flow" runat="server" TargetControlID="panelFlow" Controller="CRP" ServicePath="../AppService/FlowExtender.asmx" ServiceMethod="GetFlowViewPage" />
  </div>
</asp:Content>
