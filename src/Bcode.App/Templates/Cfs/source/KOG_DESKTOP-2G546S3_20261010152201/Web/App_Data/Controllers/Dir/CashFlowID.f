<?xml version="1.0" encoding="utf-8"?>

<!DOCTYPE dir [
  <!ENTITY ScriptIrregular SYSTEM "..\Include\Javascript\Irregular.txt">
  <!ENTITY CheckIrregular SYSTEM "..\Include\XML\CheckIrregular.txt">
  <!ENTITY defaultForm "v20GLTC6">
  <!ENTITY AfterUpdate "exec FastBusiness$Report$UpdateReportForm @@table, @form">
  <!ENTITY Set "select @form = @c, @cach_tinh = case when @tk = '' then @cach_tinh else '' end">
  <!ENTITY Check "
if (@cach_tinh &lt;&gt; '') begin
  create table #t(b bit)
  insert into #t(b) exec FastBusiness$Report$CheckReportForm @@action, @@table, @form, @ma_so, @cach_tinh
  if exists(select 1 from #t where b = 0) begin
    select 'cach_tinh' as field, replace(@$updateConflict, char(37) + 's', rtrim(@cach_tinh)) as message
    drop table #t
    return
  end
  drop table #t
end
">
]>

<dir table="v20gltc6" code="stt, ma_so, form" order="stt, ma_so, form" xmlns="urn:schemas-fast-com:data-dir">
  <title v="báo cáo lưu chuyển tiền tệ theo phương pháp gián tiếp" e="Cash Flow under the Indirect Method"></title>
  <fields>
    <field name="form" isPrimaryKey="true" disabled="true" hidden="true" readOnly="true">
      <header v="" e=""></header>
      <footer v="&lt;div class=&quot;Break&quot;/&gt;" e="&lt;div class=&quot;Break&quot;/&gt;"></footer>
    </field>
    <field name="stt" isPrimaryKey="true" allowNulls="false" type="Decimal" dataFormatString="###0" clientDefault="Default">
      <header v="Stt" e=" Number"></header>
      <items style="Numeric"></items>
      <clientScript><![CDATA[<Encrypted>iAnF8giecKm6/C7xJpljrVwL9krC0bAGPeGClE9fbiliMy937TiZ9gTvTA7KPGl7</Encrypted>]]></clientScript>
    </field>
    <field name="ma_so" isPrimaryKey="true" dataFormatString="X" allowNulls="false" clientDefault="Default">
      <header v="Mã số" e="Code"></header>
      <items style="Mask"></items>
      <clientScript><![CDATA[<Encrypted>iAnF8giecKm6/C7xJpljrVwL9krC0bAGPeGClE9fbiliMy937TiZ9gTvTA7KPGl7</Encrypted>]]></clientScript>
    </field>
    <field name="stt_in" clientDefault="Default" align="right">
      <header v="Stt, thứ tự khi in" e="Order, Number"></header>
    </field>
    <field name="ma_so_in" clientDefault="Default">
      <header v="Mã số, mã số khi in" e="Code, Code When Print"></header>
    </field>
    <field name="chi_tieu" allowNulls="false" clientDefault="Default">
      <header v="Chỉ tiêu" e="Norm "></header>
    </field>
    <field name="chi_tieu2" clientDefault="Default">
      <header v="Chỉ tiêu khác" e="Other Norm"></header>
    </field>
    <field name="thuyet_minh" clientDefault="Default">
      <header v="Thuyết minh" e="Interpretation"></header>
    </field>

    <field name="in_ck" dataFormatString="0, 1" clientDefault="Default" defaultValue="1" align="right">
      <header v="In" e="Print"></header>
      <footer v="1 - Có in, 0 - Không in" e="1 - Print, 0 - No Print"></footer>
      <items style="Mask"/>
    </field>
    <field name="bold" dataFormatString="0, 1" clientDefault="Default" defaultValue="0" align="right">
      <header v="Kiểu chữ" e="Font Bold"></header>
      <footer v="1 - Đậm, 0 - Không đậm" e="1 - Bold, 0 - Regular"></footer>
      <items style="Mask"/>
    </field>

    <field name="tk" clientDefault="Default">
      <header v="Các tài khoản" e="Accounts"></header>
    </field>
    <field name="tk_du" clientDefault="Default">
      <header v="Các tài khoản đối ứng" e="Reference Accounts"></header>
    </field>
    <field name="ten_tk%l" readOnly="true" external="true" defaultValue="''" clientDefault="Default">
      <header v="" e=""></header>
    </field>
    <field name="khong_am" dataFormatString="0, 1" clientDefault="Default" defaultValue="0" align="right">
      <header v="Lấy giá trị không âm" e="None Negative Values"></header>
      <footer v="1 - Có, 0 - Không" e="1 - Yes, 0 - No"></footer>
      <items style="Mask"/>
    </field>
    <field name="dau" dataFormatString="0, 1" clientDefault="Default" defaultValue="0" align="right">
      <header v="Thu/Chi" e="Receipt/Disbursement"></header>
      <footer v="1 - Thu, 0 - Chi" e="1 - Receipt, 0 - Disbursement"></footer>
      <items style="Mask"/>
    </field>
    <field name="no_co" dataFormatString="1, 2" clientDefault="Default" defaultValue="1" align="right">
      <header v="Phân loại" e="Classify"></header>
      <footer v="1 - Nợ, 2 - Có" e="1 - Debit, 2 - Credit"></footer>
      <items style="Mask"/>
    </field>
    <field name="dau_cuoi" dataFormatString="1, 2" clientDefault="Default" defaultValue="1" align="right">
      <header v="Đầu/Cuối" e="Begin/End"></header>
      <footer v="1 - Đầu, 2 - Cuối" e="1 - Begin, 2 - End"></footer>
      <items style="Mask"/>
    </field>
    <field name="cong_no" dataFormatString="0, 1" clientDefault="Default" defaultValue="0" align="right">
      <header v="Loại" e="AR/AP Items"></header>
      <footer v="1 - Lấy chi tiết một vế của các đối tượng công nợ, 0 - Không" e="1 - Yes, 0 - No"></footer>
      <items style="Mask"/>
    </field>

    <field name="cach_tinh" clientDefault="Default" dataFormatString="X">
      <header v="Công thức" e="Formula"></header>
      <items style="Mask"></items>
    </field>
    <field name="kind" dataFormatString="0, 1, 2" clientDefault="Default" defaultValue="0" align="right">
      <header v="Cách tính" e="Calculating Way"></header>
      <footer v="0 - Tính theo mã số, 1 - Tính theo số phát sinh, 2 - Tính theo số dư" e="0 - Base on Formula, 1 - On Account Rising, 2 - On Account Balance"></footer>
      <items style="Mask"/>
      <clientScript><![CDATA[<Encrypted>iAnF8giecKm6/C7xJpljrVwL9krC0bAGPeGClE9fbiliMy937TiZ9gTvTA7KPGl7</Encrypted>]]></clientScript>
    </field>
    <field name="type" dataFormatString="0, 1" clientDefault="0" defaultValue="0" align="right">
      <header v="Kiểu phát sinh" e="Rising Type"></header>
      <footer v="1 - Chỉ tính kỳ cuối, 0 - Không" e="1 - Only calculate for the ending month, 0 - All report time duration"></footer>
      <items style="Mask"/>
    </field>
  </fields>

  <views>
    <view id="Dir" height="132">
      <item value="8, 112, 40, 40, 40, 40, 20, 250"/>
      <item value="1011---1: [stt_in].Label, [stt], [stt_in], [form]"/>
      <item value="101010---: [ma_so].Label, [ma_so], [ma_so_in]"/>
      <item value="10100000: [chi_tieu].Label, [chi_tieu]"/>
      <item value="10100000: [chi_tieu2].Label, [chi_tieu2]"/>
      <item value="10100000: [thuyet_minh].Label, [thuyet_minh]"/>
      <item value="10000000: [form].Description"/>

      <item value="10110000: [in_ck].Label, [in_ck], [in_ck].Description"/>
      <item value="10110000: [bold].Label, [bold], [bold].Description"/>
      <item value="10000000: [form].Description"/>

      <item value="10110000: [kind].Label, [kind], [kind].Description"/>
      <item value="10110000: [khong_am].Label, [khong_am], [khong_am].Description"/>
      <item value="10110000: [dau].Label, [dau], [dau].Description"/>
      <item value="10110000: [no_co].Label, [no_co], [no_co].Description"/>
      <item value="10110000: [dau_cuoi].Label, [dau_cuoi], [dau_cuoi].Description"/>
      <item value="10110000: [type].Label, [type], [type].Description"/>
      <item value="10110000: [cong_no].Label, [cong_no], [cong_no].Description"/>
      <item value="10100000: [tk].Label, [tk]"/>
      <item value="10100000: [tk_du].Label, [tk_du]"/>
      <item value="10100000: [cach_tinh].Label, [cach_tinh]"/>
    </view>
  </views>

  <commands>
    <command event="Loading">
      <text>
        <![CDATA[<Encrypted>Bf+Hq04ftZ3NcjhBF1I/tnmtvz4Dc0yN+mNro6s83uU7/iE1v8pQzNy0bpRXj9J3xjZHl48pUwmsaoTuhwxBEiqcGcscx6+HzLi6harziWg=</Encrypted>]]>
      </text>
    </command>

    <command event="Scattering">
      <text>
        <![CDATA[<Encrypted>Bf+Hq04ftZ3NcjhBF1I/tnmtvz4Dc0yN+mNro6s83uU7/iE1v8pQzNy0bpRXj9J3xjZHl48pUwmsaoTuhwxBEiqcGcscx6+HzLi6harziWg=</Encrypted>]]>
      </text>
    </command>

    <command event="Declare">
      <text>
        <![CDATA[<Encrypted>V0eWiflnUDfNv7YNoxd6fmSlvBRYsAVaniBlIR461LlTiUcnJ3WayE31ERqRg3GtyGHbVg71XVZDb6XQhMhjybIaQ9kYldknnVCSvx8fC5rLAe6RLWSdzsO8lRNRhGcbYm/bUu+7MV2dDenI4dL2jtpE0NaDfPMSGLX3DV4RvVoG4bvBL+LdVNFz4EhOTPKUEprzj31BgRAWnucGe0Gtup+9qNWKNEh9iZczKLIJtU7YExqmAG4MLmJdJCBLRyjIFlINC2lNu+Y/cjNOQ2gkGrH84ZVOfn0cJHWRcpt8WvffKygX9OvxVukxU+kozqBWwLlrMIlaBfxWrUNFDVq+1YaQvr9hNSgRpogeeHvn8P4=</Encrypted>]]>
      </text>
    </command>

    <command event="Inserting">
      <text>
        &Set;
        <![CDATA[<Encrypted>SscxvCQrThdRRK/5ojOAw3IZCNhMNo9nSTJ5IRsFp9Mjg+HbfNKW4vFRCxWWMXZfZeV11yooLzUWZUBSnn/92PBHiOO9KQuaUTgZA5k18WE=</Encrypted>]]>&defaultForm;<![CDATA[<Encrypted>ghCYxqq8c5fAvflIh4nDRntDffP4HTJAyQF0ngt7nl8ILiM8eBL7zgWUwDleqtuvuMTTSwSUFnFV/Hlcn+Ui1/LEY+1HHXk4Dm+mbUIh/fTZDnUgXwEJaLxQEf46fjqR4VQpCqz9PkGuqiQxiUSaWRTA0cDQbw6zhvndx66gX71KSfcd3xHptna4vu8oWhIIj+3AC7Jq5zIwjIxKW0IJsfkZlDMM1HSHtDCI0RTitxkhncwCKmjDKEsNIR23QL07EXUqOfpldjFLB1QvcqNFtsQgUf5KSz6d+EOq28+q17c+qaWI6pyK3Yplo7v1/5vcj0yd/w84ibidGvQwNZBPPT8l17IV8gtuOeBpW50ScVytcTnl2uYzzKY0qCCCFhhSkYTcmdLIbqzGEBGosQpvDg==</Encrypted>]]>
        &Check;
      </text>
    </command>

    <command event="Inserted">
      <text>
        &AfterUpdate;
        <![CDATA[<Encrypted>Bf+Hq04ftZ3NcjhBF1I/tis38GA87gMW3WDhxrXDXwhwvOrQa6J2UChnr3NeqXktZsM7vgXUoa4YaBYviuhbeQ==</Encrypted>]]>
      </text>
    </command>

    <command event="Updating">
      <text>
        &Set;
        &Check;
      </text>
    </command>

    <command event="Updated">
      <text>
        &AfterUpdate;
      </text>
    </command>

    <command event="Deleting">
      <text>
        <![CDATA[<Encrypted>/vyQWCnb6YQxOe/6VlsHl637H153BJPW/IfTaf9dsuPk2Yg1aihdJln3gtFExp2Y7aNAQOgfVWWekgnL/Ro8Mf6DoVdCpdQSaAzmpdOGhseylOQ0hKtWLIY6iN8HbRlFGLHHv1If+44DSOQc32PS2jMKtkLzlZ+/1cmu60Hmy2MYstDh6M4RZX+HNP93vguN3YzBDsv28VpU7PqtyCgSlXQ70RaXWbh8qhHEZyGUAgUy2nmCDq/p+1n/VqZ2Ebhguec/1bwqRhAVULwX6+mQv8u+mKeXBwUJQ1Lf9dF9Nm0BWGk2Vm7yJtXa/I5wEmsXBUcQ8w/bDmbsCXTLv9wcdHsFliQrUFBQyCWXT7bh82EH4hGvGIzaRQdnDc6WhE4s+uxm8Kc+XUKwe+GOAkH9Asfl6w0xGFL55vX4I0YwQ17jW1xYAcN6c4EdBeOkLqLBplSL0+zR3g+xcv+QLQBx9A==</Encrypted>]]>
      </text>
    </command>

    &CheckIrregular;
  </commands>

  <script>
    <text>
      <![CDATA[<Encrypted>t6tsWO6wof8RLzeWQj1BvGYvd3TYRorupA3E/HLV+h3aGhCijbjp/KecTOmWglCPLy0xSAwp0/igQfhlTVpimXHl068BhEv/4gq3n2SETrjErfATv2lOcGuju53Q/ucpd3GHgE7dRuRyG4dOA/3beTKJoBkd/4/ej592hrdhEGM+fl9Z+BIjCyg2KZhMQjJqL5J5LSXSwGAghg7DJKvDEb5ffnvykJuF85eHsy28BsSPXqXnUJ38pCy9A8P1zc12BDa5RGtO7Vuf7YGSpE4SiCtCM580y+CpOhnuOXytxRg9D1xZ2YXdjZQlsj4YQe+Pp012DKr6Mhe1sdVzx2jOeky/rZa1BtaDtwQHXDxqMt6HI5qcTLfUrEzqgEEXpb3Ti6nbrT7PC3EMwtVn+pOt8Og6dYx8KXgk+bN84VqBDZZc5loDnBdgWkQpfNp7wvleLEGSEFR3k0aCsuoM7BloTL7fc9tPIwrhqTIYnh3xNmZFtpQCAd8uxrWBHLfxP14/9SbFsJ8NKfVJe1jRWs5Gsa0em6rxqHU1xEjACPSECU+TJlKs2gX8kEtBCsLEHs129DLOJeZ6qYs3pOTo0LR879bj9KXOVz0ck/oKW6FYOT+SSz4dctz+hYtZZiOI/Dnr7arTnoVrCOW0ipHqFq0ZQg==</Encrypted>]]>
      &ScriptIrregular;
    </text>
  </script>

  <css>
    <text>
      <![CDATA[<Encrypted>pAUjwHBdC/JYo28gWIu/LPYJD9ZGKGIeLk3PGob7Y7lVqxnU4KCBl9hnECVLtokEDjipQL6n8YKsynI4fZL9gtLYJMzZ6L54ByZsWMR6bwkPu9YfBklaselRZMHwDIOLxR469QSmaZsUAIfaBh52drerrcEBOqckyq8ZA/Cd7YlJ2jWe4Ggx99B9xtt60ii9QIV074uTHIO/aUMxs98l6iVLlKlKDrM0acoLMPFcfxo=</Encrypted>]]>
    </text>
  </css>
</dir>