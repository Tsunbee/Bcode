<?xml version="1.0" encoding="utf-8"?>

<!DOCTYPE dir [
  <!ENTITY ScriptIrregular SYSTEM "..\Include\Javascript\Irregular.txt">
  <!ENTITY CheckIrregular SYSTEM "..\Include\XML\CheckIrregular.txt">
  <!ENTITY defaultForm "v20GLTC5">
  <!ENTITY AfterUpdate "exec FastBusiness$Report$UpdateReportForm @@table, @form">
  <!ENTITY Set "select @form = @c, @cach_tinh = case when @tk_no + @tk_co = '' then @cach_tinh else '' end">
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

<dir table="v20gltc5" code="stt, ma_so, form" order="stt, ma_so, form" xmlns="urn:schemas-fast-com:data-dir">
  <title v="báo cáo lưu chuyển tiền tệ theo phương pháp trực tiếp" e="Cash Flow under the Direct Method"></title>
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
    <field name="bold" dataFormatString="0, 1" clientDefault="Default" defaultValue="1" align="right">
      <header v="Kiểu chữ" e="Font Bold"></header>
      <footer v="1 - Đậm, 0 - Không đậm" e="1 - Bold, 0 - Regular"></footer>
      <items style="Mask"/>
    </field>

    <field name="dau" dataFormatString="0, 1" clientDefault="Default" defaultValue="1" align="right">
      <header v="Thu/Chi" e="Receipt/Disbursement"></header>
      <footer v="1 - Thu, 0 - Chi" e="1 - Receipt, 0 - Disbursement"></footer>
      <items style="Mask"/>
    </field>
    <field name="tk_no" clientDefault="Default">
      <header v="Các tài khoản nợ" e="Debit Accounts"></header>
    </field>
    <field name="tk_co" clientDefault="Default">
      <header v="Các tài khoản có" e="Credit Accounts"></header>
    </field>

    <field name="cach_tinh" clientDefault="Default" dataFormatString="X">
      <header v="Công thức" e="Formula"></header>
      <items style="Mask"></items>
    </field>
    <field name="kind" dataFormatString="0, 1, 2" clientDefault="Default" defaultValue="0" align="right">
      <header v="Cách tính" e="Calculating Way"></header>
      <footer v="0 - Tính theo mã số, 1 - Tính theo số phát sinh, 2 - Tính theo số dư đầu kỳ" e="0 - Base on Formula, 1 - On Account Rising, 2 - On Opening Balance"></footer>
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
      <item value="101010---: [ma_so].Label, [ma_so],[ma_so_in]"/>
      <item value="10100000: [chi_tieu].Label, [chi_tieu]"/>
      <item value="10100000: [chi_tieu2].Label, [chi_tieu2]"/>
      <item value="10100000: [thuyet_minh].Label, [thuyet_minh]"/>
      <item value="10000000: [form].Description"/>

      <item value="10110000: [in_ck].Label, [in_ck], [in_ck].Description"/>
      <item value="10110000: [bold].Label, [bold], [bold].Description"/>
      <item value="10000000: [form].Description"/>

      <item value="10110000: [kind].Label, [kind], [kind].Description"/>
      <item value="10110000: [dau].Label, [dau], [dau].Description"/>
      <item value="10110000: [type].Label, [type], [type].Description"/>
      <item value="-1100000: [tk_no].Label, [tk_no]"/>
      <item value="-1100000: [tk_co].Label, [tk_co]"/>
      <item value="-1100000: [cach_tinh].Label, [cach_tinh]"/>
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
      <![CDATA[<Encrypted>etCVN6p5NHP7XjGZ9Yxgew27+g5K4ObuYOzuX3aersukYLI16dmuz4lbnKvXtI/qygreiJ38fvADfn0IrcQGPsFwBcK45Xao2yiwnRwndQo0CfKdPXY6r/W1G7zSkm3zhRTdpOB11kPMB7p6DT1TTLNJXWRACCSpaF3b6iQSBk7w3XAxdHbbuJAn2RqgkL843RC3mbAe0F+b6G/2alqu6wg1zcXtQ5MiqAANElbmNAvax/8S8DLI03eHHwarLvqiQl+fc4tZMqI1bIWuG7Thl5+jhEnFYzP1ADg8ujOfc7LbfzdBQpwm63XlAUdyRmGGrt5I8qwKASZl0LXWzqFnl2vwKMbNd7fZE01aDFmWOm3OxcfbZ+JVu06FuJLiMR5jft9vQvF21dOvf2slbfPQNwWp3KfsTHA4Ksvu7DuMBdPKx8Mjy18IuGzHLeWI781vAqdjN8/edq4cMpoGtpUDwGjkcjFscBACAA1SZVilmsaKqS7moiAwNzrCQh1wpHFVsVIuT79yWbu3xqZmk+fC1DAdKDUR5AiI+xocJVC+gsGY3QLMbkemHgrTxXYz4jTiCuNoIvNc2+NpkSgxbeQPwA==</Encrypted>]]>
      &ScriptIrregular;
    </text>
  </script>

  <css>
    <text>
      <![CDATA[<Encrypted>pAUjwHBdC/JYo28gWIu/LPYJD9ZGKGIeLk3PGob7Y7lVqxnU4KCBl9hnECVLtokEDjipQL6n8YKsynI4fZL9gtLYJMzZ6L54ByZsWMR6bwkPu9YfBklaselRZMHwDIOLxR469QSmaZsUAIfaBh52drerrcEBOqckyq8ZA/Cd7YlJ2jWe4Ggx99B9xtt60ii9QIV074uTHIO/aUMxs98l6iVLlKlKDrM0acoLMPFcfxo=</Encrypted>]]>
    </text>
  </css>
</dir>