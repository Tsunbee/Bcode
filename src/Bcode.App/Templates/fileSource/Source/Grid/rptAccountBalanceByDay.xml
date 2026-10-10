<?xml version="1.0" encoding="utf-8"?>

<!DOCTYPE grid [
  <!ENTITY XMLWhenReportLoading SYSTEM "..\Include\XML\WhenReportLoading.xml">
  <!ENTITY XMLWhenReportClosing SYSTEM "..\Include\XML\WhenReportClosing.xml">
  <!ENTITY XMLStandardReportToolbar SYSTEM "..\Include\XML\StandardReportToolbar.xml">
  <!ENTITY JavascriptReportInit SYSTEM "..\Include\Javascript\ReportInit.txt">

  <!ENTITY % Control.Filter SYSTEM "..\Include\Filter.ent">
  %Control.Filter;
  <!ENTITY % Repetition SYSTEM "..\Include\Repetition.ent">
  %Repetition;
]>

<grid type="Report" filter="&Repetition.Key.001;" repetition="&Repetition.Key.002;" valid="systotal = 1" xmlns="urn:schemas-fast-com:data-grid">
  <title v="Bảng cân đối phát sinh theo ngày của một tài khoản" e="Account Daily Balance"></title>
  <subTitle v="Tài khoản: %s1, từ ngày %d1 đến ngày %d2..." e="Account: %s1, Date from %d1 to %d2..."></subTitle>

  <fields>
    <field name="ngay_ct" type="DateTime" dataFormatString="@datetimeFormat" width ="80" allowSorting="true" allowFilter="true" hyperlinkFormatString="~/AppHandler/View.ashx Query: {Page: 'query_glcd3', Controller: 'rptAccountBalanceByDay', Name: '[ngay]', Value: '[ngay] + this._queryFilterString'}, Script: 'beforeDrillDown(this);'">
      <header v="Ngày ct" e="VC. Date"/>
    </field>

    <field name="du_dk_nt" type="Decimal" width="120" dataFormatString="@foreignCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Dư đầu nt" e="FC Opening Balance"></header>
    </field>
    <field name="ps_no_nt" type="Decimal" width="120" dataFormatString="@foreignCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;" aggregate="Sum">
      <header v="Phát sinh nợ nt" e="FC Debit Amount"></header>
    </field>
    <field name="ps_co_nt" type="Decimal" width="120" dataFormatString="@foreignCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;" aggregate="Sum">
      <header v="Phát sinh có nt" e="FC Credit Amount"></header>
    </field>
    <field name="du_ck_nt" type="Decimal" width="120" dataFormatString="@foreignCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Dư cuối nt" e="FC Closing Balance"></header>
    </field>

    <field name="du_dk" type="Decimal" width="120" dataFormatString="@baseCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Dư đầu" e="Opening Balance"></header>
    </field>
    <field name="ps_no" type="Decimal" width="120" dataFormatString="@baseCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;" aggregate="Sum">
      <header v="Phát sinh nợ" e="Debit Amount"></header>
    </field>
    <field name="ps_co" type="Decimal" width="120" dataFormatString="@baseCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;" aggregate="Sum">
      <header v="Phát sinh có" e="Credit Amount"></header>
    </field>
    <field name="du_ck" type="Decimal" width="120" dataFormatString="@baseCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Dư cuối" e="Closing Balance"></header>
    </field>

    <field name="ngay" width="0" hidden ="true">
      <header v="" e=""></header>
    </field>
  </fields>
  <views>
    <view id="Grid">
      <field name="ngay_ct"/>

      <field name="du_dk_nt"/>
      <field name="ps_no_nt"/>
      <field name="ps_co_nt"/>
      <field name="du_ck_nt"/>

      <field name="du_dk"/>
      <field name="ps_no"/>
      <field name="ps_co"/>
      <field name="du_ck"/>

      <field name="ngay"/>
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

  &XMLStandardReportToolbar;

</grid>