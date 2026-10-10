<?xml version="1.0" encoding="utf-8"?>

<!DOCTYPE grid [
  <!ENTITY XMLWhenReportLoading SYSTEM "..\Include\XML\WhenReportLoading.xml">
  <!ENTITY XMLWhenReportClosing SYSTEM "..\Include\XML\WhenReportClosing.xml">
  <!ENTITY JavascriptReportInit SYSTEM "..\Include\Javascript\ReportInit.txt">

  <!ENTITY % Control.Filter SYSTEM "..\Include\Filter.ent">
  %Control.Filter;
]>

<grid type="Report" xmlns="urn:schemas-fast-com:data-grid">
  <title v="Báo cáo ngân sách" e="Budget Report"></title>
  <subTitle v="%f, báo cáo theo %r, từ kỳ %p1 năm %y1 đến kỳ %p2 năm %y2..." e="%f, Report by %r, From Period %p1 Year %y1 to Period %p2 Year %y2..."></subTitle>
  <pivot rowField="xRow" columnField="xColumn" dataFields="gt_ns, gt_ns_nt, gt_th, gt_th_nt, gt_cl, gt_cl_nt" indexTable="2" indexColumn="1" indexHeader="2" indexView="3"/>
  <fields>
    <field name="stt_in" width="60" allowSorting="true" allowFilter="true">
      <header v="Stt" e="No."></header>
    </field>
    <field name="chi_tieu" width="300" allowSorting="true" allowFilter="true">
      <header v="Chỉ tiêu" e="Norm"></header>
    </field>
    <field name="ma_so_in" width="100" allowSorting="true" allowFilter="true">
      <header v="Mã số" e="Number"></header>
    </field>
    <field name="gt_ns_nt" type="Decimal" width="120" dataFormatString="@foreignCurrencyAmountViewFormat" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Ngân sách nt" e="FC Budget"></header>
    </field>
    <field name="gt_ns" type="Decimal" width="120" dataFormatString="@baseCurrencyAmountViewFormat" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Ngân sách" e="Budget"></header>
    </field>
    <field name="gt_th_nt" type="Decimal" width="120" dataFormatString="@foreignCurrencyAmountViewFormat" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Thực hiện nt" e="FC Performance"></header>
    </field>
    <field name="gt_th" type="Decimal" width="120" dataFormatString="@baseCurrencyAmountViewFormat" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Thực hiện" e="Performance"></header>
    </field>
    <field name="gt_cl_nt" type="Decimal" width="120" dataFormatString="@foreignCurrencyAmountViewFormat" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Chênh lệch nt" e="FC Variance"></header>
    </field>
    <field name="gt_cl" type="Decimal" width="120" dataFormatString="@baseCurrencyAmountViewFormat" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Chênh lệch" e="Variance"></header>
    </field>
  </fields>

  <views>
    <view id="Grid">
      <field name="stt_in"/>
      <field name="chi_tieu"/>
      <field name="ma_so_in"/>
    </view>
  </views>

  <commands>
    &XMLWhenReportLoading;
    &XMLWhenReportClosing;
  </commands>

  <script>
    <text>
      &JavascriptReportInit;
    </text>
  </script>

  <toolbar>
    <button command="Search">
      <title v="Toolbar.Search" e="Toolbar.Search"></title>
    </button>
    <button command="Print">
      <title v="Toolbar.Print" e="Toolbar.Print"></title>
    </button>
    <button command="Export">
      <title v="Toolbar.Export" e="Toolbar.Export"></title>
    </button>
    <button command="Seprate">
      <title v="-" e="-"></title>
    </button>
    <button command="Freeze">
      <title v="Toolbar.Freeze" e="Toolbar.Freeze"></title>
    </button>
  </toolbar>
</grid>