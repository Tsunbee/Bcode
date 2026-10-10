<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Hóa đơn mua hàng trong nước" e="Domestic Purchase Invoice"%>
<%@ Register Assembly="FastBusiness.QueryExtender" Namespace="FastBusiness.QueryExtender" TagPrefix="FastBusiness" %>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
		<asp:Panel ID="ResourcePanel" runat="server" Width="0" Height="0">
				<script type="text/javascript" src="../AppHandler/ExternalScript.ashx?t=PDF&m=1&v=1"></script>
		</asp:Panel>
	
		<asp:Panel ID="ExtenderPanel" runat="server" Width="0" Height="0"></asp:Panel>
		<script runat="server">
				Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
			Dim f As String = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Extender.txt"
						If IO.File.Exists(f) Then
								Dim v As String = IO.File.ReadAllText(f)
								If v = "INCLUDE" Then
										ResourcePanel.Controls.Add(LoadControl("ExtenderControls.ascx"))
								End If
						End If
						ExtenderPanel.Controls.Add(LoadControl("Chart.ascx"))
						f = HttpContext.Current.Server.MapPath("~\App_Data\Controllers\Include") & "\Multiviewer.txt"
						If IO.File.Exists(f) Then
								Dim v As String = IO.File.ReadAllText(f)
								If v = "INCLUDE" Then
										ResourcePanel.Controls.Add(LoadControl("MultiViewer.ascx"))
								End If
						End If
				End Sub
		</script>
	

		<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true" Controller="PVTran"/>
		<FastBusiness:QueryExtender runat="server" TargetControlID="queryMainContainer" ID="PVTran" GridID="MainReport" Controller="PVTran" SinglePage="false" />
	<div id="queryInitGrid" style="display:none;">
				<asp:Panel ID="queryMainContainer" runat="server" CssClass="QueryMainContainer" />
		</div>
	 
		<div id="voucherContainer" style="width:100%;height:100%"></div>
		<div id="voucherMaster" style="width:100%;height:50%;display:none;">
				<asp:Panel ID="panelReport" runat="server" />
		</div>
		<div id="voucherDetail" style="width:100%;">
				<asp:Panel ID="panelDetail" runat="server" />
		</div>
		<div style="display: none;" id="subReport">
		
		<script type="text/javascript">
document.addEventListener("DOMContentLoaded", function() {
	// Quan sát toàn bộ body
	var observer = new MutationObserver(function(mutationsList) {
			mutationsList.forEach(function(mutation) {
					mutation.addedNodes.forEach(function(node) {
							if (node.nodeType === 1 && node.id && node.id.indexOf("_Button_Lookup_ViewSelectorMenu") !== -1 && true) {
								if (node.childNodes.length === 1) {
									var lkID = node.id.replace('_ViewSelectorMenu', '');
									var lkObject = FastBusiness.AjaxControlExtender.AutoCompleteExtender.find(lkID);
									
									if (lkObject.get_dirController() == 'Contract') {
										var newLink = document.createElement("a");
										newLink.id = lkID + '_zContract1';
										newLink.href = "#";
										newLink.className = "ContextMenuItem CustomIcon";
										newLink.innerHTML = (lkObject._language == 'v' ? 'Gốc, KT, CN' : 'Original, Economic, Transfer');
										newLink.onclick = function() {
													lkObject._rowSelected = false;
													lkObject._activeRow = 0;
													
													lkObject._dirExtender = createDirExtender$(lkObject, 'zContract1', lkObject._cookie + 'zContract1');
													lkObject._dirExtender.set_values(lkObject._getPrimaryKeyValues());
													//lkObject._dirExtender.executeAction('New');

													return false;
											};
										node.appendChild(newLink);
										
										var newLink = document.createElement("a");
										newLink.id = lkID + '_Contract';
										newLink.href = "#";
										newLink.className = "ContextMenuItem CustomIcon";
										newLink.innerHTML = (lkObject._language == 'v' ? '2' : '2');
										newLink.onclick = function() {
													lkObject._rowSelected = false;
													lkObject._activeRow = 0;
													
													console.log(lkObject.get_dirService());
													//lkObject.createDirExtender();
													lkObject._dirExtender = createDirExtender$(lkObject, 'zContract4', lkObject._cookie + 'zContract4');
													lkObject._dirExtender.set_values(lkObject._getPrimaryKeyValues());

console.log(lkObject._dirExtender);
													return false;
											};
										node.appendChild(newLink);
									}
								}
							}
					});
			});
	});

	observer.observe(document.body, { childList: true, subtree: true });
});

function createDirExtender$(lk, dirController, _cookie) {
	var _dirExtender = $create(FastBusiness.AjaxControlExtender.DirExtender, {
			id: lk._lookupID + dirController + "_dirExtender_",
			viewMode: !1,
			parentType: "Lookup",
			language: lk._language,
			controller: dirController,
			servicePath: lk.get_dirService(),
			serviceLookup: lk._servicePath,
			baseUrl: lk.get_baseUrl(),
			cookie: _cookie
	}, null, null, lk._container);
	
	_dirExtender.grid = lk;
	return _dirExtender;
}
		</script>
</asp:Content>