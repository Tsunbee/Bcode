<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Phát hành phiếu bán hàng" e="Bill Issue"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <script src="../ClientScript/AES.js?v=2" type="text/javascript"></script>
    <script src="../ClientScript/CRC32.js" type="text/javascript"></script>
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" Controller="ECTran" FilterMode="true"/>
</asp:Content>