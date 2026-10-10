<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Kiểm tra tình trạng chứng từ khấu trừ thuế TNCN" e="Certificate of Personal Income Tax Withholding Status Verification"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div>
        <asp:Panel ID="panelReport" runat="server"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="hrptTaxDeductionStatusVerification" FilterMode="true"/>
</asp:Content>