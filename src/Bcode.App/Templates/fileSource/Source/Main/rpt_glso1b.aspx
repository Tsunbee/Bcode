<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Sổ theo dõi thuế GTGT được hoàn lại" e="Ledger of Refund VAT"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="rptLedgerOfReturnVATAccount" FilterMode="true"/>
    
</asp:Content>