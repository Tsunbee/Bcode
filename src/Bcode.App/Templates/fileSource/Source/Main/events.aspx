<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Lịch làm việc" e="Calendar" %>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server">
	<asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0"></asp:Panel>
	<script runat="server">
        Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
            ResourcePanel.Controls.Add(LoadControl("TreeView.ascx"))
        End Sub
    </script>
	
    <script src="../ClientScript/jquery.min.js" type="text/javascript"></script>

    <script type="text/javascript" src='../ClientScript/ViewPanel.js'></script>
    
    <script type="text/javascript" src="../AppHandler/ExternalScript.ashx?t=Calendar&m=1&v=1" charset="utf-8"></script>
    <link type="text/css" rel="stylesheet" href="../AppHandler/ExternalCss.ashx?t=Calendar&v=1" />
    
    <link rel="stylesheet" href="../Css/ViewPanel.css" type="text/css" />

    <script type="text/javascript" charset="utf-8">
        function initTaskPage(g) {
            var o = $get('eventSplitter');
            var task = $create(FastManagement.Calendar, {
                id: 'CalendarExtender',
                grid: g,
                language: g._language,
                servicePath: '../AppService/ManagementService.asmx',
                serviceTaskMethod: 'GetData',
                serviceUserMethod: 'GetChildrenUsers',
                schedulerId: 'divSchedulerDetail',
                mainSplitter: 'mpMainBody',
                eventSplitter: 'eventSplitter',
                divTreeUsers: 'divTreeUsers',
                divScheduler: 'divScheduler',
                mainContent: 'mpMainBody'
            }, null, null, o);
            g._task = task;
        }
    </script>

</asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
    <FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="Events" />
    <div id="eventSplitter" class="ManagementLayoutFull" style="border-top: 1px solid #ddd;">
    </div>
    <div style="display: none">
        <div id="divTreeUsers" class="ManagementLayoutFull" style="display: block; white-space: nowrap; vertical-align: top;" />
        <div id="divScheduler" class="ManagementLayoutFull">
            <table cellpadding="0" cellspacing="0" class="ManagementLayoutFullScroll">
                <tr>
                    <td style="height: 20px" valign="middle">
                        <div id="divScheduler_Toolbar" style="padding: 2px 0 0 14px; vertical-align: middle;">
                        </div>
                    </td>
                </tr>
                <tr>
                    <td class="ManagementLayoutFullScroll">
                        <div id="divSchedulerDetail" class="dhx_cal_container ManagementLayoutFullScroll">
                        </div>
                        <div id="grid" style="display: none;">
                            <asp:Panel ID="panelReport" runat="server" />
                        </div>
                    </td>
                </tr>
            </table>
        </div>

        <script src="../ClientScript/Calendar.js?v=1" type="text/javascript"></script>
    </div>
</asp:Content>
