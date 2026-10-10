<?xml version="1.0" encoding="utf-8"?>

<!DOCTYPE grid [
  <!ENTITY XMLWhenReportLoading SYSTEM "..\Include\XML\WhenReportLoading.xml">
  <!ENTITY XMLWhenReportClosing SYSTEM "..\Include\XML\WhenReportClosing.xml">
  <!ENTITY XMLStandardReportToolbar SYSTEM "..\Include\XML\StandardReportToolbar.xml">
  <!ENTITY JavascriptReportInit SYSTEM "..\Include\Javascript\ReportInit.txt">
  <!ENTITY TransferID "rptCustomerDebtComparisonBook">

  <!ENTITY % PrintRight SYSTEM "..\Include\PrintRightGrid.ent">
  %PrintRight;

  <!ENTITY % Control.Filter SYSTEM "..\Include\Filter.ent">
  %Control.Filter;

  <!ENTITY % Repetition SYSTEM "..\Include\Repetition.ent">
  %Repetition;
]>

<grid type="Report" filter="&Repetition.Key.012;" repetition="&Repetition.Key.013;" valid="systotal = 1" xmlns="urn:schemas-fast-com:data-grid">
  <title v="Sổ đối chiếu công nợ của một khách hàng" e="Customer AR/AP Comparison Book"></title>
  <subTitle v="Tài khoản: %s1, khách hàng: %s2%s3, hóa đơn từ ngày %d1 đến ngày %d2..." e="Account: %s1, Customer: %s2%s3, Invoice Date from %d1 to %d2..."></subTitle>

  <fields>
    <field name="ngay_ct" type="DateTime" dataFormatString="@datetimeFormat" width ="80" allowSorting="true" allowFilter="true">
      <header v="Ngày ht" e="VC. Date" />
    </field>
    <field name="ngay_lct" type="DateTime" dataFormatString="@datetimeFormat" width ="80" allowSorting="true" allowFilter="true">
      <header v="Ngày ct" e="GL Date" />
    </field>
    <field name="ma_ct0" width="60" allowSorting="true" allowFilter="true" hyperlinkFormatString="~/AppHandler/Voucher.ashx Query: {Name: '[ma_ct]', Value: '[stt_rec]'}, Script: 'beforeDrillDown(this);'">
      <header v="Mã ct" e="VC. Code"></header>
    </field>
    <field name="so_ct" width="80" allowSorting="true" allowFilter="true" align="right">
      <header v="Số ct" e="Voucher No."></header>
    </field>
    <field name="ngay_ct0" type="DateTime" dataFormatString="@datetimeFormat" width ="80" allowSorting="true" allowFilter="true">
      <header v="Ngày hóa đơn" e="Invoice Date" />
    </field>
    <field name="so_ct0" width="80" allowSorting="true" allowFilter="true" align="right">
      <header v="Số hóa đơn" e="Invoice No."></header>
    </field>
    <field name="dien_giai" width="300" allowSorting="true" allowFilter="true">
      <header v="Diễn giải" e="Description"></header>
    </field>

    <field name="ps_no_nt" type="Decimal" width="120" dataFormatString="@foreignCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;" aggregate="Sum">
      <header v="Phát sinh nợ nt" e="FC Debit Amount"></header>
    </field>
    <field name="ps_co_nt" type="Decimal" width="120" dataFormatString="@foreignCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;" aggregate="Sum">
      <header v="Phát sinh có nt" e="FC Credit Amount"></header>
    </field>
    <field name="du_no_nt" type="Decimal" width="120" dataFormatString="@foreignCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Dư nợ nt" e="FC Debit Bal."></header>
    </field>
    <field name="du_co_nt" type="Decimal" width="120" dataFormatString="@foreignCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Dư có nt" e="FC Credit Bal."></header>
    </field>

    <field name="ma_nt" width="60" allowSorting="true" allowFilter="true">
      <header v="Mã nt" e="FC Code"></header>
    </field>
    <field name="ty_gia" type="Decimal" width="100" dataFormatString="@exchangeRateViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Tỷ giá" e="Exchange Rate"></header>
    </field>

    <field name="ps_no" type="Decimal" width="120" dataFormatString="@baseCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;" aggregate="Sum">
      <header v="Phát sinh nợ" e="Debit Amount"></header>
    </field>
    <field name="ps_co" type="Decimal" width="120" dataFormatString="@baseCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;" aggregate="Sum">
      <header v="Phát sinh có" e="Credit Amount"></header>
    </field>
    <field name="du_no" type="Decimal" width="120" dataFormatString="@baseCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Dư nợ" e="Debit Balance"></header>
    </field>
    <field name="du_co" type="Decimal" width="120" dataFormatString="@baseCurrencyAmountViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Dư có" e="Credit Balance"></header>
    </field>

    <field name="so_luong" type="Decimal" width="100" dataFormatString="@quantityViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;" aggregate="Sum">
      <header v="Số lượng" e="Quantity"></header>
    </field>
    <field name="gia_nt" type="Decimal" width="100" dataFormatString="@foreignCurrencyPriceViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Giá nt" e="FC Price"></header>
    </field>
    <field name="tien_nt" type="Decimal" dataFormatString="@foreignCurrencyAmountViewFormat" width="120" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;" aggregate="Sum">
      <header v="Tiền nt" e="FC Amount"></header>
    </field>
    <field name="gia" type="Decimal" width="100" dataFormatString="@baseCurrencyPriceViewFormat" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;">
      <header v="Giá" e="Price"></header>
    </field>
    <field name="tien" type="Decimal" dataFormatString="@baseCurrencyAmountViewFormat" width="120" allowSorting="true" allowFilter="&GridReportAllowFilter.Number;" aggregate="Sum">
      <header v="Tiền" e="Amount"></header>
    </field>

    <field name="ma_ct" width="0" hidden="true">
      <header v="" e=""></header>
    </field>
    <field name="stt_rec" width="0" hidden="true">
      <header v="" e=""></header>
    </field>
  </fields>

  <views>
    <view id="Grid">
      <field name="ngay_ct"/>
      <field name="ngay_lct"/>
      <field name="ma_ct0"/>
      <field name="so_ct"/>
      <field name="ngay_ct0"/>
      <field name="so_ct0"/>
      <field name="dien_giai"/>


      <field name="ps_no_nt"/>
      <field name="ps_co_nt"/>
      <field name="du_no_nt"/>
      <field name="du_co_nt"/>

      <field name="ma_nt"/>
      <field name="ty_gia"/>

      <field name="ps_no"/>
      <field name="ps_co"/>
      <field name="du_no"/>
      <field name="du_co"/>

      <field name="so_luong"/>
      <field name="gia_nt"/>
      <field name="tien_nt"/>
      <field name="gia"/>
      <field name="tien"/>

      <field name="ma_ct"/>
      <field name="stt_rec"/>
    </view>
  </views>

  <commands>
    &XMLWhenGridShowingPrintRight;
    &XMLWhenReportLoading;
    &XMLWhenReportClosing;
  </commands>

  <script>
    <text>
      &JavascriptReportInit;
      &PrintRightScript;
    </text>
  </script>

  &XMLStandardReportToolbar;
</grid>