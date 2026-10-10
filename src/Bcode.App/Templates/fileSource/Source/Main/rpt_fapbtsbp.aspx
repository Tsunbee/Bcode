<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Bảng phân bổ khấu hao TSCĐ theo bộ phận" e="Fixed Asset Depreciation Allocation by Department Report"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
<div>
<asp:Panel ID="panelReport" runat="server"/>
</div>
<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="rptFADepreciationAllocationByDepartment" FilterMode="true"/>
</asp:Content>
