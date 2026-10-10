<%@ Page Title="Business Center" Language="vb" AutoEventWireup="false" MasterPageFile="~/Main/Server.master"
    CodeBehind="smbc.aspx.vb" Inherits="smbc.smbc" v="Phân tích tổng hợp tình hình kinh doanh"
    e="Synthetic Analysis of Performance" %>

<%@ Register Assembly="DevExpress.Web.v11.2, Version=11.2.7.0, Culture=neutral, PublicKeyToken=b88d1754d700e49a"
    Namespace="DevExpress.Web.ASPxSplitter" TagPrefix="dx" %>
<asp:Content ID="Content1" ContentPlaceHolderID="head" runat="server">
    <link href="<%= ResolveUrl("~/Css/bistyle.css") %>" rel="Stylesheet" type="text/css" />
</asp:Content>
<asp:Content ID="Content2" ContentPlaceHolderID="FastBusiness" runat="server">
    <table cellpadding="0" cellspacing="0" border="0" class="page">
        <tr>
            <td valign="top" style="width: 100%; height: 100%;">
                <dx:ASPxSplitter ID="oSplitter" ClientInstanceName="oSplitter" runat="server" Height="100%"
                    Width="100%" Orientation="Vertical">
                    <Panes>
                        <dx:SplitterPane ShowCollapseBackwardButton="True" ShowCollapseForwardButton="True"
                            MinSize="200px">
                            <PaneStyle>
                                <Paddings Padding="0px" />
                            </PaneStyle>
                            <Panes>
                                <dx:SplitterPane ShowCollapseBackwardButton="True" ShowCollapseForwardButton="True"
                                    ScrollBars="Auto" MinSize="200px" Name="SplitterPane1">
                                    <PaneStyle CssClass="bgcolor">
                                        <Paddings Padding="0px" />
                                    </PaneStyle>
                                    <ContentCollection>
                                        <dx:SplitterContentControl ID="SplitterContentControl1" runat="server">
                                            <asp:UpdatePanel ID="uPanel1" runat="server" UpdateMode="Conditional">
                                                <ContentTemplate>
                                                    <asp:DropDownList ID="ocbb1" runat="server" AutoPostBack="True" CssClass="cbbFull"
                                                        EnableViewState="true">
                                                    </asp:DropDownList>
                                                    <asp:Panel ID="Panel1" runat="server" Height="94%" Width="100%" HorizontalAlign="Center">
                                                    </asp:Panel>
                                                </ContentTemplate>
                                            </asp:UpdatePanel>
                                        </dx:SplitterContentControl>
                                    </ContentCollection>
                                </dx:SplitterPane>
                                <dx:SplitterPane ShowCollapseBackwardButton="True" ShowCollapseForwardButton="True"
                                    ScrollBars="Auto" MinSize="200px" Name="SplitterPane2">
                                    <PaneStyle CssClass="bgcolor">
                                        <Paddings Padding="0px" />
                                    </PaneStyle>
                                    <ContentCollection>
                                        <dx:SplitterContentControl ID="SplitterContentControl2" runat="server">
                                            <asp:UpdatePanel ID="uPanel2" runat="server" UpdateMode="Conditional">
                                                <ContentTemplate>
                                                    <asp:DropDownList ID="ocbb2" runat="server" AutoPostBack="True" CssClass="cbbFull"
                                                        EnableViewState="true">
                                                    </asp:DropDownList>
                                                    <asp:Panel ID="Panel2" runat="server" Height="94%" Width="100%" HorizontalAlign="Center">
                                                    </asp:Panel>
                                                </ContentTemplate>
                                            </asp:UpdatePanel>
                                        </dx:SplitterContentControl>
                                    </ContentCollection>
                                </dx:SplitterPane>
                            </Panes>
                            <ContentCollection>
                                <dx:SplitterContentControl ID="SplitterContentControl3" runat="server">
                                </dx:SplitterContentControl>
                            </ContentCollection>
                        </dx:SplitterPane>
                        <dx:SplitterPane ShowCollapseBackwardButton="True" ShowCollapseForwardButton="True"
                            ScrollBars="Auto" MinSize="200px" Name="SplitterPane3">
                            <PaneStyle CssClass="bgcolor">
                                <Paddings Padding="0px" />
                            </PaneStyle>
                            <Panes>
                                <dx:SplitterPane ShowCollapseBackwardButton="True" ShowCollapseForwardButton="True"
                                    MinSize="200px" Name="SplitterPane3">
                                    <PaneStyle>
                                        <Paddings Padding="0px" />
                                    </PaneStyle>
                                    <ContentCollection>
                                        <dx:SplitterContentControl ID="SplitterContentControl4" runat="server">
                                            <asp:UpdatePanel ID="uPanel3" runat="server" UpdateMode="Conditional">
                                                <ContentTemplate>
                                                    <asp:DropDownList ID="ocbb3" runat="server" AutoPostBack="True" CssClass="cbbFull"
                                                        EnableViewState="true">
                                                    </asp:DropDownList>
                                                    <asp:Panel ID="Panel3" runat="server" Height="94%" Width="100%" HorizontalAlign="Center">
                                                    </asp:Panel>
                                                </ContentTemplate>
                                            </asp:UpdatePanel>
                                        </dx:SplitterContentControl>
                                    </ContentCollection>
                                </dx:SplitterPane>
                                <dx:SplitterPane ShowCollapseBackwardButton="True" ShowCollapseForwardButton="True"
                                    ScrollBars="Auto" MinSize="200px" Name="SplitterPane4">
                                    <PaneStyle CssClass="bgcolor">
                                        <Paddings Padding="0px" />
                                    </PaneStyle>
                                    <ContentCollection>
                                        <dx:SplitterContentControl ID="SplitterContentControl5" runat="server">
                                            <asp:UpdatePanel ID="uPanel4" runat="server" UpdateMode="Conditional">
                                                <ContentTemplate>
                                                    <asp:DropDownList ID="ocbb4" runat="server" AutoPostBack="True" CssClass="cbbFull"
                                                        EnableViewState="true">
                                                    </asp:DropDownList>
                                                    <asp:Panel ID="Panel4" runat="server" Height="94%" Width="100%" HorizontalAlign="Center">
                                                    </asp:Panel>
                                                </ContentTemplate>
                                            </asp:UpdatePanel>
                                        </dx:SplitterContentControl>
                                    </ContentCollection>
                                </dx:SplitterPane>
                            </Panes>
                            <ContentCollection>
                                <dx:SplitterContentControl ID="SplitterContentControl6" runat="server">
                                </dx:SplitterContentControl>
                            </ContentCollection>
                        </dx:SplitterPane>
                    </Panes>
                </dx:ASPxSplitter>
            </td>
        </tr>
    </table>
</asp:Content>
