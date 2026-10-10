<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Khai báo thông tin thư điện tử" e="Mail Server Declaration" %>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server">     
	 <script src="../ClientScript/AES.js" type="text/javascript"></script>
</asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <div style="display:none">
        <input style="display:none" type="text" name="fakeusernameremembered"/>
        <input style="display:none" type="password" name="fakepasswordremembered"/>
    </div>
    <div><asp:Panel ID="panelReport" runat="server" /></div>
    <FastBusiness:ReportExtender id="MainReport" runat="server" targetcontrolid="panelReport" readonly="true" controller="MailServer" />
</asp:Content>
