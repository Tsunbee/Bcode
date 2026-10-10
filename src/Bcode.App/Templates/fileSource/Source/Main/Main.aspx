<%@ Page Language="vb" AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" title="Fast Business Online" v="Fast Business Online" e="Fast Business Online"%>
<asp:Content ID="Content1" ContentPlaceHolderID="head" runat="server">
</asp:Content>
<asp:Content ID="Content2" ContentPlaceHolderID="FastBusiness" runat="server">
    <br />
    <div>
        <asp:Panel ID="panelReport" runat="server" style="display:none"/>
    </div>
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="Main"/>
</asp:Content>