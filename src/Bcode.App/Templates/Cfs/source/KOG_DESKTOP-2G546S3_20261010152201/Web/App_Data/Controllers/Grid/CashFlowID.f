<?xml version="1.0" encoding="utf-8"?>

<!DOCTYPE grid [
]>
<grid table="v20GLTC6" code="form, stt, ma_so" order="form, stt, ma_so" xmlns="urn:schemas-fast-com:data-grid">
  <title v="Báo cáo lưu chuyển tiền tệ theo phương pháp gián tiếp" e="Cash Flow under the Indirect Method"></title>
  <subTitle v="Mẫu báo cáo: %s" e="Report Form: %s"></subTitle>

  <fields>
    <field name="form" isPrimaryKey="true" width="" hidden="true">
      <header v="" e=""></header>
    </field>
    <field name="stt" isPrimaryKey="true" width="80" align="right" allowFilter="true" allowSorting="true">
      <header v="Stt" e="No."></header>
    </field>
    <field name="ma_so" isPrimaryKey="true" width="100" allowFilter="true" allowSorting="true">
      <header v="Mã số" e="Code"></header>
    </field>
    <field name="chi_tieu%l" width="300" dataFormatString="X" allowFilter="true" allowSorting="true">
      <header v="Chỉ tiêu" e="Norm"></header>
    </field>
    <field name="cach_tinh" width="300" dataFormatString="X" allowFilter="true" allowSorting="true">
      <header v="Công thức" e="Formula"></header>
    </field>
    <field name="tk" width="150" dataFormatString="X" allowFilter="true" allowSorting="true">
      <header v="Tài khoản" e="Account"></header>
    </field>
    <field name="tk_du" width="150" dataFormatString="X" allowFilter="true" allowSorting="true">
      <header v="Tài khoản đối ứng" e="Reference Acct."></header>
    </field>
  </fields>

  <views>
    <view id="Grid">
      <field name="form"/>
      <field name="stt"/>
      <field name="ma_so"/>
      <field name="chi_tieu%l"/>
      <field name="cach_tinh"/>
      <field name="tk"/>
      <field name="tk_du"/>
    </view>
  </views>

  <commands>
    <command event="Loading">
      <text>
        <![CDATA[<Encrypted>gA91vUZ2vNGmtdILK3a3dmIlv6zC/cGU/8w2/WE0ySfZR9SaBkFBPFEA2yJDvdK8KIt4b8sW/hlCcaquspv/p8RvOeACgzM/dEYPrGMmA8c=</Encrypted>]]>
      </text>
    </command>

    <command event="Closing">
      <text>
        <![CDATA[<Encrypted>gA91vUZ2vNGmtdILK3a3dhtf1dfDAlKwKj6VT+BF0Sx7L07UclUNEuE8CpB42PhZrzWrWax8Z0rLvDNaOcMSkxNqpwfSeCyVErGXxjlF+A8=</Encrypted>]]>
      </text>
    </command>
  </commands>

  <script>
    <text>
      <![CDATA[<Encrypted>wqH4RlwClGAEvCHvyhf7RI+mIjlf3gKSS+CQfzDWuLJuGrEeSblp0/Mb5+rCUej6Y4oH5zMRYzz4woNyqBJQ+NpADh/u67afGnsy+fN6EbZ48oldElXoDAH/I/2612bb4c+wM7JPxGr1JaHVCgGWIQc37lLBowH/DCbURkQsI6fp2j8Z5Msg8O03Q5wmOjddPAMpIp/H41h6GfGbwBU8sdiZYRo60qeP0RRBYNtndHkTTwkjbj8+29Ni08hiohGfySYJGqTnjzHusEV9t5bE0lPkewvgiuRKkWMr4zKQ218=</Encrypted>]]>
    </text>
  </script>

  <toolbar>
    <button command="New">
      <title v="Toolbar.New" e="Toolbar.New"></title>
    </button>
    <button command="Edit">
      <title v="Toolbar.Edit" e="Toolbar.Edit"></title>
    </button>
    <button command="Delete">
      <title v="Toolbar.Delete" e="Toolbar.Delete"></title>
    </button>
    <button command="Search">
      <title v="Toolbar.Search" e="Toolbar.Search"></title>
    </button>
    <button command="View">
      <title v="Toolbar.View" e="Toolbar.View"></title>
    </button>
    <button command="Export">
      <title v="Toolbar.Export" e="Toolbar.Export"/>
    </button>
    <button command="Freeze">
      <title v="Toolbar.Freeze" e="Toolbar.Freeze"></title>
    </button>
  </toolbar>

</grid>