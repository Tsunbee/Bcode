<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Chứng từ chưa nhập theo đối tượng tập hợp chi phí" e="Voucher has not yet Inputted as Cost Factor Object Setting"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="rptCOCheckObjectNotInput" FilterMode="true"/>
</asp:Content>