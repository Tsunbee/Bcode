<%@ Page Title="FAST Olap" Language="vb" AutoEventWireup="false" MasterPageFile="~/Main/Server.master"
    CodeBehind="smOlap.aspx.vb" Inherits="smOlap.smOlap" %>

<%@ Register Assembly="DevExpress.Web.ASPxPivotGrid.v11.2.Export, Version=11.2.7.0, Culture=neutral, PublicKeyToken=b88d1754d700e49a"
    Namespace="DevExpress.Web.ASPxPivotGrid.Export" TagPrefix="dx" %>
<%@ Register Assembly="DevExpress.Web.v11.2, Version=11.2.7.0, Culture=neutral, PublicKeyToken=b88d1754d700e49a"
    Namespace="DevExpress.Web.ASPxSplitter" TagPrefix="dx" %>
<%@ Register Assembly="DevExpress.XtraCharts.v11.2.Web, Version=11.2.7.0, Culture=neutral, PublicKeyToken=b88d1754d700e49a"
    Namespace="DevExpress.XtraCharts.Web" TagPrefix="dxchartsui" %>
<%@ Register Assembly="DevExpress.Web.ASPxEditors.v11.2, Version=11.2.7.0, Culture=neutral, PublicKeyToken=b88d1754d700e49a"
    Namespace="DevExpress.Web.ASPxEditors" TagPrefix="dxe" %>
<%@ Register Assembly="DevExpress.Web.v11.2, Version=11.2.7.0, Culture=neutral, PublicKeyToken=b88d1754d700e49a"
    Namespace="DevExpress.Web.ASPxMenu" TagPrefix="dxm" %>
<%@ Register Assembly="DevExpress.XtraCharts.v11.2, Version=11.2.7.0, Culture=neutral, PublicKeyToken=b88d1754d700e49a"
    Namespace="DevExpress.XtraCharts" TagPrefix="cc1" %>
<%@ Register Assembly="DevExpress.Web.ASPxPivotGrid.v11.2, Version=11.2.7.0, Culture=neutral, PublicKeyToken=b88d1754d700e49a"
    Namespace="DevExpress.Web.ASPxPivotGrid" TagPrefix="dx" %>
<asp:Content ID="Content1" ContentPlaceHolderID="head" runat="server">
    <link href="../Css/OlapStyle.css" rel="stylesheet" type="text/css" />
</asp:Content>
<asp:Content ID="Content2" ContentPlaceHolderID="FastBusiness" runat="server">

    <script type="text/javascript">
        function OnSplitterPaneResized(s, e) {
            if (e.pane.name == 'splitterPaneTop') {
                chart.SetWidth(e.pane.GetClientWidth());
                chart.SetHeight(e.pane.GetClientHeight());
            }
        }
    </script>

    <script language="vb" runat="server">
                
        Protected Function Cube() As String
            Return IIf(smOlap.smOlap.Language = "V", "Chọn khối dữ liệu", "Select Data Cube")
        End Function
        Protected Function SaveLayoutTT() As String
            Return IIf(smOlap.smOlap.Language = "V", "Lưu trạng thái", "Save Layout")
        End Function
        Protected Function LoadLayoutTT() As String
            Return IIf(smOlap.smOlap.Language = "V", "Mở trạng thái đã lưu", "Open Layout")
        End Function
        Protected Function SaveGridPdf() As String
            Return IIf(smOlap.smOlap.Language = "V", "Kết xuất ra PDF", "Export to PDF")
        End Function
        Protected Function SaveGridExcel() As String
            Return IIf(smOlap.smOlap.Language = "V", "Kết xuất ra Excel", "Export to Excel")
        End Function
        Protected Function RowPerPage() As String
            Return IIf(smOlap.smOlap.Language = "V", "Số bản ghi một trang", "Items per page")
        End Function
        Protected Function ShowTotal() As String
            Return IIf(smOlap.smOlap.Language = "V", "Hiện dòng, cột tổng", "Show Total")
        End Function
        Protected Function GetPivotGrid() As String
            Return IIf(smOlap.smOlap.Language = "V", "Bảng:", "PivotGrid:")
        End Function
        Protected Function GetChart() As String
            Return IIf(smOlap.smOlap.Language = "V", "Đồ thị:", "Chart:")
        End Function
        Protected Function GetAppearance() As String
            Return IIf(smOlap.smOlap.Language = "V", "Kiểu hiển thị", "Chart Appearance")
        End Function
        Protected Function GetPalette() As String
            Return IIf(smOlap.smOlap.Language = "V", "Bảng màu", "Palette")
        End Function
        Protected Function ExportType() As String
            Return IIf(smOlap.smOlap.Language = "V", "Chọn định dạng xuất", "Chose Export Type")
        End Function
        Protected Function ChartType() As String
            Return IIf(smOlap.smOlap.Language = "V", "Dạng đồ thị", "Chart Type")
        End Function
        Protected Function Refresh() As String
            Return IIf(smOlap.smOlap.Language = "V", "Làm tươi", "Refresh")
        End Function
    </script>

    <div style="height: 100%; width: 100%;">
        <dx:ASPxSplitter ID="splitter" runat="server" Height="100%" Width="100%" Orientation="Vertical">
            <Panes>
                <dx:SplitterPane Name="splitterMain">
                    <Panes>
                        <dx:SplitterPane MaxSize="500px" MinSize="250px" Name="splitterLeft" ShowCollapseBackwardButton="True"
                            ShowCollapseForwardButton="False" Size="250px">
                            <PaneStyle>
                                <Paddings Padding="0px" />
                            </PaneStyle>
                            <ContentCollection>
                                <dx:SplitterContentControl runat="server">
                                    <dx:ASPxPivotCustomizationControl ID="pivotCustomizationControl" runat="server" AllowFilter="True"
                                        ASPxPivotGridID="pivotGrid" DeferredUpdates="True" Height="100%" Width="100%">
                                    </dx:ASPxPivotCustomizationControl>
                                </dx:SplitterContentControl>
                            </ContentCollection>
                        </dx:SplitterPane>
                        <dx:SplitterPane Name="splitterRignt">
                            <Panes>
                                <dx:SplitterPane Name="splitterPaneTop" ShowCollapseBackwardButton="True" ShowCollapseForwardButton="True">
                                    <ContentCollection>
                                        <dx:SplitterContentControl runat="server">
                                            <dxchartsui:WebChartControl ID="chartControl" runat="server" ClientInstanceName="chart"
                                                DataSourceID="pivotGrid" Height="340px" OnCustomCallback="chart_CustomCallback"
                                                SeriesDataMember="Series" Width="1000px">
                                                <diagramserializable>
                                                    <cc1:XYDiagram>
                                                        <axisx visibleinpanesserializable="-1">
                                                            <label resolveoverlappingmode="HideOverlapped" staggered="True" />
                                                            <range sidemarginsenabled="True" />
                                                        </axisx>
                                                        <axisy visibleinpanesserializable="-1">
                                                            <range sidemarginsenabled="True" />
                                                            <numericoptions format="Number" precision="0"/>
                                                        </axisy>
                                                    </cc1:XYDiagram>
                                                </diagramserializable>
                                                <fillstyle>
                                                    <optionsserializable>
                                                        <cc1:SolidFillOptions />
                                                    </optionsserializable>
                                                </fillstyle>
                                                <legend maxhorizontalpercentage="30"></legend>
                                                <seriestemplate argumentdatamember="Arguments" valuedatamembersserializable="Values">
                                                    <viewserializable>
                                                        <cc1:SideBySideBarSeriesView>
                                                        </cc1:SideBySideBarSeriesView>
                                                    </viewserializable>
                                                    <labelserializable>
                                                        <cc1:SideBySideBarSeriesLabel LineVisible="True" Visible="False">
                                                            <fillstyle>
                                                                <optionsserializable>
                                                                    <cc1:SolidFillOptions />
                                                                </optionsserializable>
                                                            </fillstyle>
                                                            <pointoptionsserializable>
                                                                <cc1:PointOptions>
                                                                    <valuenumericoptions format="Number" />
                                                                </cc1:PointOptions>
                                                            </pointoptionsserializable>
                                                        </cc1:SideBySideBarSeriesLabel>
                                                    </labelserializable>
                                                    <legendpointoptionsserializable>
                                                        <cc1:PointOptions>
                                                            <valuenumericoptions format="Number" />
                                                        </cc1:PointOptions>
                                                    </legendpointoptionsserializable>
                                                </seriestemplate>
                                                <clientsideevents endcallback="function (s, e) {
	for (var i = 0; i &lt; cbPalette.GetItemCount(); i++) {
		if (cbPalette.GetItem(i).value == s.GetChart().paletteName) {
			cbPalette.SetSelectedIndex(i);
		}
	}
}" />
                                            </dxchartsui:WebChartControl>
                                        </dx:SplitterContentControl>
                                    </ContentCollection>
                                </dx:SplitterPane>
                                <dx:SplitterPane Name="splitterPaneBottom" ScrollBars="Vertical" ShowCollapseBackwardButton="False"
                                    ShowCollapseForwardButton="True">
                                    <ContentCollection>
                                        <dx:SplitterContentControl runat="server">
                                            <dx:ASPxPivotGrid ID="pivotGrid" runat="server" AreaStyle-BackColor="White" ClientInstanceName="pivotGrid"
                                                Height="100%" OnCustomCallback="pivotGrid_CustomCallback" Width="100%">
                                                <ClientSideEvents AfterCallback="function(s, e) { chart.PerformCallback(&quot;Refresh&quot;); }" />
                                                <OptionsView ShowColumnGrandTotals="False" ShowFilterHeaders="False" ShowHorizontalScrollBar="True"
                                                    ShowRowGrandTotals="False" />
                                                <OptionsChartDataSource FieldValuesProvideMode="DisplayText" />
                                                <OptionsPager NumericButtonCount="15">
                                                </OptionsPager>
                                                <OptionsData FilterByVisibleFieldsOnly="True" />
                                                <OptionsFilter ShowHiddenItems="True" ShowOnlyAvailableItems="True" />
                                                <Styles>
                                                    <AreaStyle BackColor="White">
                                                        <Paddings Padding="2px" />
                                                    </AreaStyle>
                                                </Styles>
                                            </dx:ASPxPivotGrid>
                                        </dx:SplitterContentControl>
                                    </ContentCollection>
                                </dx:SplitterPane>
                            </Panes>
                            <PaneStyle>
                                <Paddings Padding="0px" />
                            </PaneStyle>
                            <ContentCollection>
                                <dx:SplitterContentControl runat="server">
                                </dx:SplitterContentControl>
                            </ContentCollection>
                        </dx:SplitterPane>
                    </Panes>
                    <PaneStyle>
                        <Paddings Padding="0" />
                    </PaneStyle>
                    <ContentCollection>
                        <dx:SplitterContentControl ID="SplitterContentControl1" runat="server">
                        </dx:SplitterContentControl>
                    </ContentCollection>
                </dx:SplitterPane>
                <dx:SplitterPane Name="splitterbar" MaxSize="29px" MinSize="29px" Size="29px" ShowCollapseBackwardButton="False"
                    ShowCollapseForwardButton="False" ShowSeparatorImage="False">
                    <Separator Size="0px">
                    </Separator>
                    <PaneStyle BackColor="#E9F4FB">
                        <Paddings Padding="0" />
                    </PaneStyle>
                    <ContentCollection>
                        <dx:SplitterContentControl ID="SplitterContentControl5" runat="server">
                            <asp:Table ID="Table1" runat="server">
                                <asp:TableRow runat="server">
                                    <asp:TableCell runat="server">
                                        <dxe:ASPxButton runat="server" AutoPostBack="False" EnableDefaultAppearance="False"
                                            ID="btnProcess">
                                            <Image Url="../Images/Olap/Process.png">
                                            </Image>
                                        </dxe:ASPxButton>
                                    </asp:TableCell>
                                    <asp:TableCell runat="server">
                                        <dxe:ASPxComboBox runat="server" Width="120px" Height="23px" AutoPostBack="True"
                                            BackColor="Transparent" ToolTip="<%# Cube() %>" ID="cbCube">
                                        </dxe:ASPxComboBox>
                                    </asp:TableCell>
                                    <asp:TableCell runat="server">
                                        <dxm:ASPxMenu runat="server" ApplyItemStyleToTemplates="True" ClientInstanceName="mnuToolbar"
                                            BackColor="Transparent" ID="mnuToolbar">
                                            <ClientSideEvents ItemClick="function(s, e) {
	if (e.item.name == 'mnuPrint')
		chart.Print();
	if (e.item.name == 'mnuSaveToDisk')
		chart.SaveToDisk(cbFormat.GetText());
	}"></ClientSideEvents>
                                            <Items>
                                                <dxm:MenuItem Name="mnuLblPivotGrid">
                                                    <Template>
                                                        <dxe:ASPxLabel ID="lblPivotGrid" runat="server" Font-Bold="True" Text="<%# GetPivotGrid() %>" />
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuGridRefresh">
                                                    <Template>
                                                        <dxe:ASPxButton ID="btnGridRefresh" runat="server" Text="" AutoPostBack="false" ToolTip="<%# Refresh() %>"
                                                            EnableDefaultAppearance="False">
                                                            <ClientSideEvents Click="function(s, e) { pivotGrid.PerformCallback(&quot;Refresh&quot;); }" />
                                                            <Image Url="../Images/Olap/Refresh.png" />
                                                        </dxe:ASPxButton>
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="pivotSlayout" Visible="False">
                                                    <Template>
                                                        <asp:ImageButton ID="btnPGSLayout" runat="server" ImageUrl="../Images/Olap/Export.png"
                                                            Height="20px" Width="20px" OnClick="btnPGSLayout_Click" ToolTip="<%# SaveLayoutTT() %>" />
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="pivotPDF">
                                                    <Template>
                                                        <asp:ImageButton ID="pivotPDF" runat="server" ImageUrl="../Images/Olap/Pdf.png" Height="20px"
                                                            Width="20px" OnClick="pivotPDF_Click" ToolTip="<%# SaveGridPdf() %>" />
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="pivotExcel">
                                                    <Template>
                                                        <asp:ImageButton ID="pivotExcel" runat="server" ImageUrl="../Images/Olap/Excel.png"
                                                            Height="20px" Width="20px" OnClick="pivotExcel_Click" ToolTip="<%# SaveGridExcel() %>" />
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuShowTotal">
                                                    <Template>
                                                        <dxe:ASPxCheckBox ID="cShowTotal" runat="server" ToolTip="<%# ShowTotal() %>">
                                                            <ClientSideEvents CheckedChanged="function(s, e) { pivotGrid.PerformCallback(&quot;ShowTotal&quot;);}" />
                                                        </dxe:ASPxCheckBox>
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuRowPerPage">
                                                    <Template>
                                                        <dxe:ASPxComboBox ID="cbRowPerPage" runat="server" ClientInstanceName="cbRowPerPage"
                                                            SelectedIndex="1" ValueType="System.String" Width="50px" BackColor="Transparent"
                                                            DropDownRows="10" AutoPostBack="false" ToolTip="<%# RowPerPage() %>" HorizontalAlign="Right">
                                                            <Items>
                                                                <dxe:ListEditItem Text="5" Value="5" />
                                                                <dxe:ListEditItem Text="10" Value="10" />
                                                                <dxe:ListEditItem Text="15" Value="15" />
                                                                <dxe:ListEditItem Text="20" Value="20" />
                                                                <dxe:ListEditItem Text="25" Value="25" />
                                                                <dxe:ListEditItem Text="30" Value="30" />
                                                                <dxe:ListEditItem Text="50" Value="50" />
                                                                <dxe:ListEditItem Text="100" Value="100" />
                                                            </Items>
                                                            <ItemStyle HorizontalAlign="Right" />
                                                            <ClientSideEvents SelectedIndexChanged="function(s, e) { pivotGrid.PerformCallback(&quot;RowPerPage&quot;); }" />
                                                        </dxe:ASPxComboBox>
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuLblPivotGrid">
                                                    <Template>
                                                        &#160;&#160;&#160;
                                                        <dxe:ASPxLabel ID="lblChart" runat="server" Font-Bold="True" Text="<%# GetChart() %>" />
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuRefresh">
                                                    <Template>
                                                        <dxe:ASPxButton ID="btnRefresh" runat="server" Text="" AutoPostBack="false" ToolTip="<%# Refresh() %>"
                                                            EnableDefaultAppearance="False">
                                                            <ClientSideEvents Click="function(s, e) { chart.PerformCallback(&quot;Refresh&quot;); }" />
                                                            <Image Url="../Images/Olap/Refresh.png" />
                                                        </dxe:ASPxButton>
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuPrint" Text="">
                                                    <Image Url="../Images/Olap/Print.png">
                                                    </Image>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem BeginGroup="True" Name="mnuSaveToDisk" Text="">
                                                    <Image Url="../Images/Olap/Save.png">
                                                    </Image>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuFormat">
                                                    <Template>
                                                        <dxe:ASPxComboBox ID="cbFormat" runat="server" ClientInstanceName="cbFormat" SelectedIndex="0"
                                                            ValueType="System.String" Width="60px" BackColor="Transparent" ToolTip="<%# ExportType() %>">
                                                            <Items>
                                                                <dxe:ListEditItem Text="pdf" Value="pdf" />
                                                                <dxe:ListEditItem Text="xls" Value="xls" />
                                                                <dxe:ListEditItem Text="png" Value="png" />
                                                                <dxe:ListEditItem Text="jpeg" Value="jpeg" />
                                                                <dxe:ListEditItem Text="bmp" Value="bmp" />
                                                                <dxe:ListEditItem Text="tiff" Value="tiff" />
                                                                <dxe:ListEditItem Text="gif" Value="gif" />
                                                            </Items>
                                                        </dxe:ASPxComboBox>
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem BeginGroup="True" Name="mnuLblAppearance">
                                                    <Template>
                                                        <dxe:ASPxLabel ID="lblAppearance" runat="server" Text="<%# GetAppearance() %>" Visible="false" />
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuAppearance">
                                                    <Template>
                                                        <dxe:ASPxComboBox ID="cbAppearance" runat="server" ClientInstanceName="cbAppearance"
                                                            ValueType="System.String" Width="120px" DropDownRows="10" BackColor="Transparent"
                                                            ToolTip="<%# GetAppearance() %>">
                                                            <ClientSideEvents SelectedIndexChanged="function(s, e) { chart.PerformCallback(&quot;Appearance&quot;); }" />
                                                        </dxe:ASPxComboBox>
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuLblPalette">
                                                    <Template>
                                                        <dxe:ASPxLabel ID="lblPalette" runat="server" Text="<%# GetPalette() %>" Visible="false" />
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuPalette">
                                                    <Template>
                                                        <dxe:ASPxComboBox ID="cbPalette" runat="server" ClientInstanceName="cbPalette" ValueType="System.String"
                                                            Width="120px" DropDownRows="10" BackColor="Transparent" ToolTip="<%# GetPalette() %>">
                                                            <ClientSideEvents SelectedIndexChanged="function(s, e) { chart.PerformCallback(&quot;Palette&quot;); }" />
                                                        </dxe:ASPxComboBox>
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuShowLabel">
                                                    <Template>
                                                        <dxe:ASPxCheckBox ID="cShowLabel" runat="server">
                                                            <ClientSideEvents CheckedChanged="function(s, e) { chart.PerformCallback(&quot;ShowLabel&quot;);}" />
                                                        </dxe:ASPxCheckBox>
                                                    </Template>
                                                </dxm:MenuItem>
                                                <dxm:MenuItem Name="mnuGraphicType">
                                                    <Template>
                                                        <dxe:ASPxComboBox ID="cbGraphicType" runat="server" ClientInstanceName="cbGraphicType"
                                                            SelectedIndex="0" ValueType="System.String" Width="120px" BackColor="Transparent"
                                                            DropDownRows="10" AutoPostBack="false" ToolTip="<%# ChartType() %>">
                                                            <ClientSideEvents SelectedIndexChanged="function(s, e) { chart.PerformCallback(&quot;GraphicType&quot;); }" />
                                                        </dxe:ASPxComboBox>
                                                    </Template>
                                                </dxm:MenuItem>
                                            </Items>
                                        </dxm:ASPxMenu>
                                    </asp:TableCell>
                                </asp:TableRow>
                            </asp:Table>
                            <dx:ASPxPivotGridExporter ID="pivotExporter" runat="server" ASPxPivotGridID="pivotGrid">
                            </dx:ASPxPivotGridExporter>
                        </dx:SplitterContentControl>
                    </ContentCollection>
                </dx:SplitterPane>
            </Panes>
            <ClientSideEvents PaneResized="OnSplitterPaneResized" />
        </dx:ASPxSplitter>
    </div>
</asp:Content>
