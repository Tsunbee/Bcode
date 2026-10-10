<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Điều chỉnh số liệu tờ khai thuế đối với hộ kinh doanh, cá nhân kinh doanh" e="Adjustment of Tax Declaration for Business Household"%>
<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" FilterMode="true" Controller="rpTaxDeclarationForBusinessHouseholdAdj"/>
</asp:Content>