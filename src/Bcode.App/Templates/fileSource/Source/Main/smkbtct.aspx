<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Khai báo sử dụng các trường" e="Add or Remove Fields" %>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
  <div>
    <asp:Panel ID="panelReport" runat="server" />
  </div>
  <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="false" FilterMode="true" Controller="FieldsDetail" InitScript="var g=this;g._resources.Pager.PageSizes=[5,10,15,20,25];g._gridPageSize=g._resources.Pager.PageSizes[3];" />
</asp:Content>
