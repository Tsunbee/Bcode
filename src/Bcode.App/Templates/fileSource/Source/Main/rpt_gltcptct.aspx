<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Tính giá trị các chỉ tiêu tài chính" e="Calculate Value of Financial Articles"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
<div>
<asp:Panel ID="panelReport" runat="server"/>
</div>
<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" FilterMode="true" ReadOnly="false" Controller="rptCalculateValueOfFinancialArticles"/>
</asp:Content>
