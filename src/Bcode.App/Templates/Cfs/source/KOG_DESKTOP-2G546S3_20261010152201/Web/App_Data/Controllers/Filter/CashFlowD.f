<?xml version="1.0" encoding="utf-8"?>

<!DOCTYPE dir [
  <!ENTITY XMLWhenFilterLoading SYSTEM "..\Include\XML\WhenFilterLoading.xml">
  <!ENTITY XMLWhenFilterClosing SYSTEM "..\Include\XML\WhenFilterClosing.xml">

  <!ENTITY defaultTable "V20GLTC5">
]>

<dir table="v20dmmaubc" code="ma_maubc, form" order="ma_maubc, form" xmlns="urn:schemas-fast-com:data-dir">
  <title v="Lọc mẫu báo cáo" e="Report Form Filter"></title>
  <fields>
    <field name="ma_maubc" categoryIndex="1" allowNulls="false" defaultValue="&defaultTable;" clientDefault="&defaultTable;" hidden="true" readOnly="true">
      <header v="Mã mẫu báo cáo" e="Report Code"></header>
    </field>
    <field name="ten_ma_maubc%l" categoryIndex="1" readOnly="true" external="true" defaultValue="Default" hidden="true">
      <header v="" e=""></header>
    </field>

    <field name="form" allowNulls="false">
      <header v="Mẫu báo cáo" e="Report Form"></header>
      <items style="AutoComplete" controller="ReportForm" reference="ten_form%l" key="form in (select form from v20dmmaubc where ma_maubc = '{$%c[ma_maubc]}') and (kieu_bc = 0 or (exists (select 1 from options a where rtrim(isnull(a.val, 0)) = v20dmmaubc.kieu_bc and name = 'm_kieu_bc')))" check="form in (select form from v20dmmaubc where ma_maubc = '{$%c[ma_maubc]}') and (kieu_bc = 0 or (exists (select 1 from options a where rtrim(isnull(a.val, 0)) = v20dmmaubc.kieu_bc and name = 'm_kieu_bc')))"/>
      <clientScript><![CDATA[<Encrypted>iAnF8giecKm6/C7xJpljrdrqc4Y01cWaD9nz2hX7HGIzS3zyB9md9Rg+HHBQnOLkJzJfzo1QIKqsgSDwM0BGaw==</Encrypted>]]></clientScript>
    </field>
    <field name="ten_form%l" categoryIndex="1" readOnly="true" external="true" defaultValue="Default">
      <header v="" e=""></header>
    </field>

    <field name="loai" external="true" dataFormatString="1, 2, 3" clientDefault="2" align="right" allowContain="true" defaultValue="Default">
      <header v="Loại" e="Type"></header>
      <footer v="1 - Tạo mẫu, 2 - Sửa mẫu, 3 - Xóa mẫu" e="1 - New, 2 - Edit, 3 - Delete"></footer>
      <items style="Mask"/>
      <clientScript><![CDATA[<Encrypted>iAnF8giecKm6/C7xJpljrdrqc4Y01cWaD9nz2hX7HGLji2JCkRsAEZq9p57PwFjct5MBI/4iNy0JzYnyWMyQZw==</Encrypted>]]></clientScript>
    </field>

    <field name="ten_maubc" categoryIndex="1">
      <header v="Tên mẫu báo cáo" e="Report Name"></header>
    </field>
    <field name="ten_maubc2" categoryIndex="1">
      <header v="Tên khác" e="Other Name"></header>
    </field>
    <field name="xoa_yn" categoryIndex="1" type="Boolean" external="true" allowContain="true" defaultValue="true">
      <header v="" e=""></header>
      <footer v="Xác nhận xóa mẫu báo cáo" e="Confirm delete"></footer>
      <items style="CheckBox"/>
    </field>

    <field name="h_line1" categoryIndex="9">
      <header v="Thông tin" e="Information"></header>
      <footer v="&lt;div class=&quot;LabelDescription&quot;&gt;Thông tin&lt;/div&gt;" e="&lt;div class=&quot;LabelDescription&quot;&gt;Information&lt;/div&gt;"></footer>
    </field>
    <field name="h_line12" categoryIndex="9">
      <header v="Thông tin khác" e="Other Information"></header>
      <footer v="&lt;div class=&quot;LabelDescription&quot;&gt;Thông tin khác&lt;/div&gt;" e="&lt;div class=&quot;LabelDescription&quot;&gt;Other Information&lt;/div&gt;"></footer>
    </field>
    <field name="h_line2" categoryIndex="9">
      <header v="" e=""></header>
    </field>
    <field name="h_line22" categoryIndex="9">
      <header v="" e=""></header>
    </field>
    <field name="h_line3" categoryIndex="9">
      <header v="" e=""></header>
    </field>
    <field name="h_line32" categoryIndex="9">
      <header v="" e=""></header>
    </field>
    <field name="h_line4" categoryIndex="9">
      <header v="" e=""></header>
    </field>
    <field name="h_line42" categoryIndex="9">
      <header v="" e=""></header>
    </field>
    <field name="h_line5" categoryIndex="9">
      <header v="" e=""></header>
    </field>
    <field name="h_line52" categoryIndex="9">
      <header v="" e=""></header>
    </field>
  </fields>

  <views>
    <view id="Dir" height="159">
      <item value="120, 20, 80, 100, 220, 10"/>
      <item value="110100: [form].Label, [form], [ten_form%l]"/>
      <item value="111000: [loai].Label, [loai], [loai].Description"/>

      <item value="110001: [ten_maubc].Label, [ten_maubc], [ma_maubc]"/>
      <item value="11000-: [ten_maubc2].Label, [ten_maubc2]"/>
      <item value="-1100-: [xoa_yn], [xoa_yn].Description"/>

      <item value="1-1: [h_line1].Desciption, [h_line12].Desciption"/>
      <item value="1-1: [h_line1], [h_line12]"/>
      <item value="1-1: [h_line2], [h_line22]"/>
      <item value="1-1: [h_line3], [h_line32]"/>
      <item value="1-1: [h_line4], [h_line42]"/>
      <item value="1-1: [h_line5], [h_line52]"/>
      <categories>
        <category index="1" columns="120, 20, 80, 100, 230, 0">
          <header v="Thông tin chung" e="General Information"/>
        </category>
        <category index="9" columns="271, 8, 271">
          <header v="Khác" e="Other"/>
        </category>
      </categories>
    </view>
  </views>

  <commands>
    <command event="Declare">
      <text>
        <![CDATA[<Encrypted>gaf/5ZcBOlguBUqpepslO0yRLFIL73+wAal7PG1wVsqJR+vj5cUC30BkPQlBlIu+WVV4s8dzSVoh6aS90ZfRUVvUF0pt1eIvXXoVZUBwPG8XbG5Q7Jh4ZbVAYGfL1yZ/eVGhNLQ9Wsi7f8XxeKDDvcYCBbvRDp7DZIp1UQQeKZqhTotqV9extoC4WmWikGmmHHfEG1PX7dgClwj7cbo+W2CVrJ96n0qdNIwGSQ/8Vj3fdY4v9YSXR0g6dwnRSFb0e5tWMJS69SO+V/yFZ9UnfTF3G7EVHUzUPXNUySqhDZ8=</Encrypted>]]>
      </text>
    </command>

    <command event="Loading">
      <text>
        <![CDATA[<Encrypted>rZlr+okgrIS5Vayrf3uPT3FOclJ4JSMyNky7u2NK1gJQHMxAhO9uyOSyL484iVJTVBR09k2BxoVNz55RtLqXI8ilTJifCuMT8iIYNjLAatIwp15cC7l3x8nViKHsqPmv1EabSdNo4CiQEJ3r+p6vV2Jsk8i6Wm9+ok5kwrbQNW6TykAxaalUNXY3t/GGeYbK7zNn6gwYHvP3OZmWKeCIwvjnJ+8R8vZDNSjPxq4raZs=</Encrypted>]]>&defaultTable;<![CDATA[<Encrypted>LmqurJ7QZk1R2ZVMxdLdyeyemInsH5wOofBqbXRQ1c6CaFJ/Ybq27BixhP7HcyI/BB3zxt8YT1W1D6CG6NAUFfSKEiwCKXbyfb4dRMkM75wGelRTB0ExDLkyGuOCbPQNP7ONRnE6/c8ccNJfpzZGsqS8Ji/tb/ERLi8FqDUsh4H9LPOcJQzzqU7QL8XvT9+p</Encrypted>]]>&defaultTable;<![CDATA[<Encrypted>zBlA77H2Z1KOZzrdMmglJMtBVy9h1uyJuQvZ8FDqHo4Jn5YYpy4k/k7wSBOpuoHBv4je4XBqHtkHoz90zZkGUkn1EogX1Aolka1ECLHbZKb0mGLjSrvh4FNJbQ9ZveBgwW89BH1WIqByuUn5QP9koY+n3Ug4bF7x2VEcaGOeS+i5m/42ExPBpWSn5cdfpGxCoWDL+KCvl5mvQfH5YcbFg9NZp5EcGjqbaceXbAahKMJSnqhfPjIC5Ha3MUL1PW5SYUIV38ooYrSHwXiwVGQyaA+6hWmGfsu8/5t4sPdSL4IuorhRcCuVt18LjUbEm0FRGU7eOi3kiUkdc1i1+bGj0JQ7gIkWZ6dxeGboe0DOOTY=</Encrypted>]]>
      </text>
    </command>

    &XMLWhenFilterClosing;

    <command event="Inserting">
      <text>
        <![CDATA[<Encrypted>fQNd0Z9t7JYx/Ln/3pl+UAgEMFWRsO/uxEUQcXNlvbRNJk07rvB9Vehvih28fqYOd28A3ikiAe4oU+sis8WW2TBhD6Rw/YgDXKnOe8mYnUCmyANx1JNdf2kImrvQ9IAaGzoA7s8a1JWRruTjBeU7i92jD8pSF66OEDqATzYpAhFgpSOFvADNC9mdz5r8ZwVd</Encrypted>]]>&defaultTable;<![CDATA[<Encrypted>tQ1TlYwoTm/bGKZiLqUbIEGpfYOKwwLnmFZGDtW00MlyQ3Mw8U6ck28APZ8PprFczsKLcwV6mwvaGQiONbl0Ql+EbOAhABJ8H1Rf6iw6KhEtXnxnmOX/Lfe2p5/faAT6Bh2afZjWHMx1+UWWjnI8lnJaDSNJmSRzx6A35elQtaTl9svYApsHmSRS1zfEcfJGij2Hwf2hMAjo3FNJkrXQWdniPdFVn9ebLao7VYerKMu/tacoL0qc7v6MpXYyrAxt9/H64foQvSGtOfJecb7NW18whbWK3M7qv1J1iE4xNdNhlLn+KHWcJg9XgB2DWfN9Mg5g8mrIky1Z2rweNrKhNy9PHFWnmITd8xk4t6hbBRgrQM24pnVlUTnDTjYawKveo/Xr0xQ+7j6oh0p43xu1C01JqLoKOy4Zu3I+zV0f6qrM6Lk9Pmc6PDIFo0FXBueE8wnFY3Ff8XFrHK194koGthnI6x0brWMl6fzqfAOyznhDtfJmNGXlSx77EXLaE0rTppS338WmB+M5bhDHLa40bc5EbztNcd823W0kDqqr7ggq02RlkVSIU8clUFEAY+nH/grthBFQo89z1GsIHc3gV5kMxoVL1eRelbKovx8t4VgQv1VKb3J1+ny+KoKIxfeqtclaeCPUb8BHxVzlCHB5c0jmVmCRgApKJaiACQVLNiUtOt8BpAN+c1wlsLwOjncGlZl4aFEajeS1hvHN4CFO94oTk2NPNmK/hAJIIWzHs8ilxL5ZH0v0WMp15mKBtc3xQ3qhXep+ny/KaAX4VYaftgoxt5tKu1OJj8e2ZH2zHrz/XJa4kSlfqJ69P9D/l+7vybi+fXMA1EywGEAI5CfZ2MnlIc9dP6GathZTgJkWraIybK7WwJqpxS8UjuosNtNIGe+Xptcdtg2wr/dhTDM5tpB76rdiTvocYDTGajSFbfwHM/ULHIyVzFvTnQitoBiUoTUKX4t4RAN2XkA0VY+1FqZv8PXMZM3T8Elr3+zu8Pgch9dNdc8Z0zy+MA+Yk2/zdpvDXyiCJpVTgkM/sDvlU2Ztz36tpR5wmT81CrEJGVcDnxGG88TTSpwXYxjLycjQQOzSz5HgWdP0LNX9sppKmNLtOA+3eCaNlbo0Ey7vKSfvv2RbMker5HbEpzmFQ3VzKbYb8aC55LWC5I6enxECXge3EG2Hy+eCFB9+bi5y4fs=</Encrypted>]]>&defaultTable;<![CDATA[<Encrypted>Nv2ZIIzxBGFwEUUyW3RIm3jF76HiiwdhU8POSvwc8IUJwITAz1Wzr1cpgA5VvTr8ezUWDuTPFaWKNbNtZ1kBqb+i9U9d/jed34t+SBiAOj0=</Encrypted>]]>&defaultTable;<![CDATA[<Encrypted>O9iR1N6RNf7ezMfhk6i86aBHvLzRDfukVoVl/q9udUxW2SNNx0dr+5EAX1AMa3ykBaCcb183rIZcbxm6V7p5DTa1BOXkN8qdSO9BlI7YXeuisQKgtw7fb96uf2QwfBkrauCG2kPFHSiB5MfW9hF3E1cLL0vi3kOvGKHaWJJMl8042dmMBaJodHhI9DyJtlJB</Encrypted>]]>
      </text>
    </command>
  </commands>

  <script>
    <text>
      <![CDATA[<Encrypted>0Qi6d+X5TSrLZCD17RYPdVXJkWwVr56gnWZqTeUMkQl2vYrANo2BeLBpBr4cbBnPLsbI9YwfTEY7/kxIdaRlOMdED45j40chuhIJYbWi4ntQKQyU4Hlvot+RAfd0fY/b2iFXVTh9IuvLGl5Qxiq8YcYUkZgH/Zy3+mZzYM9xKT6hQBjit37HbMfSqWZDlAA8JmkXFOUy6THgtvmyamRDrc4Gb6ZlTmPveoyfLGZ6VpVgcxDRsjZjP4n26CIRt5zJdsv+ln3GrecE4daiaFj5DIxHh1qE6D+X0ae6hPdXW2a7HSiuP8wviSelPjC9U9uflQOCi6RauRIWJCLgxQGlqfhwUBeNK1fJjfht/limQu5oyWAi53FZC0jA9dKrUGOasaZkkW+d/RuBgjQVvn0B+uWf1/Ew+eOBI7j0gOmgBL0DznW+N8WivhPC8fjfsbTEMAx1NAUUXOCM3P1zreHKq+2rBwkhOH1qd0vkEHXXez+eChXprHFJ7LDIQSO0K0MSB+7PSmtRDpQSAR9WiAhL3k4imUg8p48d24Hb2CRwECrQyCqP3HPp+qb+0Q5owVNc9GybEr4EopvVC0HbmBosBvgVEMyaoRWmfjnNM9ixr6OXKMlfo3Dw2hh/98gERDlr7AZY9pwg/iOjLDiItLc3ml50mVkXu0/iiQZfyUoarrdN7SaFbNZS7JmFrvTxB+qGYyHmP1nppuWZ9h8W1dCpMMZ1VLsEpfTLCLQkcVF6+wmYMNieiHFr75PmGcU4T0wvQwQLhIGo0G/kzoa3faLMWseJhmzAMgX/jlNAvaVq2SDcsHkdYPg3vsyjCuE+oTFmjvA3eFXVLH55kCa8Flgq6VOn6Bi1WA/wAOg8nal+aFitVUTtnzBlK5IQ1vS9NqZKAL3OKCoiiT91El/P03hgMBAUR8Hi0RtZQRhs5Kzg3hubK9R6ASriqcNkUrjyFlOcbgg5vkS+QnzhwooKevNIFXL09vZm4WtJ2SBqLmPO65c3jY1pWvmwggyQgnImNzA/OkxPTJShQERW4Iyjxb8SktWegbHFhA8VUROJdHVg6C4nlAclkVOH8mLy4OMeq7OabWAkWoYtYbV7+PjCz2omUM/xfZT9Pbw61qO/5mTKTidLu+YOiMhC2x+9REaFtQlg</Encrypted>]]>
    </text>
  </script>

  <response>
    <action id="ReportForm">
      <text>
        <![CDATA[<Encrypted>dbRx54ppl6Epu+IIR+2sO6S67abLKQNEewn3SzudKKlpuWzUVvF8xKMOeXfAGto4ng7fc9kS4qcf54OSCHueTa0I+zVmIPKHixeL7LAOuLm05jw0T70k9ovPd08gsSp4ZjvceVr6dxXZpUNZQtiTl5e/KOnzdAWO73SIFtC2qQIfdmFGLb9e0Vkwl7kei/pa</Encrypted>]]>
      </text>
    </action>
  </response>

  <css>
    <text>
      <![CDATA[<Encrypted>pC4bVA4aPykvPqxhz18awwL9Oyu9xxjFbPGzePqRA+E7sWZtFI/rEpglyw8NO1vR8G151TPVINklMZG3XnjcnzQCmKYCFqec8bLDKB6w7pk13YDwvzguFtfbcOY2mkxY</Encrypted>]]>
    </text>
  </css>
</dir>