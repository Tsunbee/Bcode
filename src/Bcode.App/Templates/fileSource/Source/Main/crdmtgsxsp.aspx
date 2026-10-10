<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Định mức thời gian sản xuất sản phẩm" e="Processing Time per Unit"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server">
</asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
  <div>
    <asp:Panel ID="panelReport" runat="server" />
  </div>
  <fastbusiness:reportextender id="MainReport" runat="server" targetcontrolid="panelReport"
    readonly="true" controller="CRProductionLeadtimeUnit" />
</asp:Content>
