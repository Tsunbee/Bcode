<?xml version="1.0" encoding="utf-8"?>

<!DOCTYPE dir [
  <!ENTITY XMLWhenFilterLoading SYSTEM "..\Include\XML\WhenFilterLoading.xml">
  <!ENTITY XMLWhenFilterClosing SYSTEM "..\Include\XML\WhenFilterClosing.xml">

  <!ENTITY defaultTable "V20GLTC1">
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
      <items style="AutoComplete" controller="ReportForm" reference="ten_form%l" key="form in (select form from v20dmmaubc where ma_maubc = '{$%c[ma_maubc]}') and (kieu_bc = 0 or exists(select 1 from options a where name = 'm_kieu_bc' and rtrim(isnull(a.val, 0)) = v20dmmaubc.kieu_bc))" check="form in (select form from v20dmmaubc where ma_maubc = '{$%c[ma_maubc]}') and (kieu_bc = 0 or exists(select 1 from options a where name = 'm_kieu_bc' and rtrim(isnull(a.val, 0)) = v20dmmaubc.kieu_bc))"/>
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
        <![CDATA[<Encrypted>fQNd0Z9t7JYx/Ln/3pl+UAgEMFWRsO/uxEUQcXNlvbRNJk07rvB9Vehvih28fqYOd28A3ikiAe4oU+sis8WW2TBhD6Rw/YgDXKnOe8mYnUCmyANx1JNdf2kImrvQ9IAaGzoA7s8a1JWRruTjBeU7i92jD8pSF66OEDqATzYpAhFgpSOFvADNC9mdz5r8ZwVd</Encrypted>]]>&defaultTable;<![CDATA[<Encrypted>V8Tvw28tohtBSLs6xD4gLLEUVjDKWYht8y768velYk67hRyTts2OtSLUZJktk4ybNOoDMgNvUBvcOVKnx1QoZOnkpkyjF5Bgb2LAs9bZC33wFw+3Qn9ZH0cp/CX1NZHXYaEaed0uUX+MMaVqdptX2GKL94i3H1C/BBt9J6mrSw+Ta61Y5JLUuuH2EcfYuS845IIamg3cifWp+BEo5bB/3dbowqGV+9dpLSn3dC/L8MssfalyvKL8qm/OLR25YChzbGZ+x83KqttMC2mMgbJiM78FBNl0msNeMTRPGAlP6dLwosDT/ptIp67nrSL402T1dTgxxpl0S4jlqy4wZ0luW84oR1SDK9y4k+44vZuR/KSnrniAzpcqjZpMKdvE6iOVA6jWVlMhDXZB5DWqjPASWJOaFoj6qfHPyzvTowGub3Ks2CNCKJGogl0SMg0bsg9NiKFdqyuNLt34yiR3n5Vvu6GqYG+jFUTOaj7Zn7NHEosqNeT1603dKOi2teEXlhBl+zrAcZdpad/O0tdkLSNBd4wUVarERhoaO2wcxNYof0Rx6FNxQKk55k3QpzefhbsHTF6kw2Z49rQnkMNUoBH/WlTQRDjenxtuppZTM7nEEtM/ndXBUUkaGsOBae1+YG8VK+ICNpU8Anw6JJfPf9VVjPGfUWpUI+Y2zxJGoNOCHOn7pPoG9cme02uqCkawdGnTuUeYBB4cPtYNmtZf8uv7lpVNK+KnL38N9/Tp0fsCbqc6kTrw9kAoVeGAENFnWMcb0Hn4xLd7Jd3KCnBssyx7QI0pIJdS0r+AovD0bRjtkI7QxwTvtINng5ag9LioZaiDWeC/46LHFd5noY07LBzHVBsJuzalGcfkAIH2hyd6FRsMJeCLrQ0RCUdbciIpvecNJgmWBDC/xUHowFzOsOKtMOIGaT6QgtI9ooyiEKnubqTBOWpU1o6r5rwWy5J4yFG71THy2p1Jm+NvGvot687pzxsrIv/acltRXcErvRtW69spninwJn55C6A4I9pHbGUFheoRcItD9hLTY+Ns3A98hj8NPJV+sDchRX9JkZO2cLWSs+wsQF/jkJG1T8As7VOTcgG2nygE9piaNzFHYVlSjXy0aDWmeRVG8WpxEc935SNiqmjVMvJrS4YBlyeTTeZxaxgBSCyw//Pnbp4bR93ih3R/WBKngeD1TyX1XbyreBk=</Encrypted>]]>&defaultTable;<![CDATA[<Encrypted>Nv2ZIIzxBGFwEUUyW3RIm3jF76HiiwdhU8POSvwc8IUJwITAz1Wzr1cpgA5VvTr8ezUWDuTPFaWKNbNtZ1kBqb+i9U9d/jed34t+SBiAOj0=</Encrypted>]]>&defaultTable;<![CDATA[<Encrypted>O9iR1N6RNf7ezMfhk6i86aBHvLzRDfukVoVl/q9udUxW2SNNx0dr+5EAX1AMa3ykBaCcb183rIZcbxm6V7p5DTa1BOXkN8qdSO9BlI7YXeuisQKgtw7fb96uf2QwfBkrauCG2kPFHSiB5MfW9hF3E1cLL0vi3kOvGKHaWJJMl8042dmMBaJodHhI9DyJtlJB</Encrypted>]]>
      </text>
    </command>
  </commands>

  <script>
    <text>
      <![CDATA[<Encrypted>lLEF8jKoWSBmRoAk2qMVldZQzPmwi9K4oknILQILcNMqVcCQsicPyEpcxKEsZMGzek7kA8obmr5pXBjz3A3pchyzTb1ja1bzWgR2uFwnXsBj3V/j0HXcCx5lTJeoxzewvAEYqugGn700+iX6JHZWJvG7KyujOc7I6F9dTm87IxLFN6+XBut0F61LqAmQ/ubApCHDmInyP2z4vvBZCLHH/nlO4GvC8cJV6Dv46kK1rhuw2MY74whFdpiRiz/7GGbu246Ap37fYCSAor6PcMyU3I9e3Ua+28CdkiLAuofkalx/yfQGJgnlkCoqLdrYASiU+LmnHkTBpM2ttFGucU1JU3km1I6kfwks53G6SH8EmBkU8vNw945MGV9EFCLrbXF0vmYOnJZ53KnJs8WY7P7ClH/9xR/gZiUetPoswg39yrRQzd5XGYhtZihzaQx4QvwYfKLONaZj0X8uJB898mXoysUC3N3NLKSshQMyTTzDn9M4ePGnhp+8EeP+yI6cXSrplEBZgVHzVNhqMO7tGwhj2kWWTZ6Wvn52HxcZf7y9+VGIGp1Py32bKwUW2ppuK28y3v07IsEQgoetsUZVcsnjbeXQIac/5+k9O6xKSJ8KMnU4oYtIqC2O2CQthFxAwniR3eDILa7ngVI8J6VMofHGYqL7aZR/ql+rcw+fOJqkegLEEifVyNnNKFDwRp/8Vp0yFf+GOxmyDTk37OGWkLB/MfJguoPPMUIfJin6MnGXGftcWwZZZO2X/pT2b/gNIh+xmsoDOSV85DjqyQL16waCRWDNOfubgmDO0MirdGyU9w69fcXujZ63RXVv43lqcHqCHpFF4YofHCALJgJNQA5CCZkmr3yfuoDPq/HAAb2oIinmpqK44YWHgvKEHK6IuU040EvH5FA+3ya2dR5hUZNMtp73XybfJUc3XVGA8kMcPH3y1Ksp1p+k67opQa07ydbhESSZw7EyF74+ybUOfaawjpwEJaERmoJa0NkHRxs8Aq8KwfpjHYEZ6KzKQkAbmR782fLP9H6/p+5vFodhGwVmMcfPo7N7wBALGa9KTs5JoILYu/jVkFzzl5U4NEmz5Z5ysbmeaR1TKcmReePN2e5IRH5uYpI54AbsDjLgpxGzdxpa8ftAp58z2kWB8z7bOgACfjmdQF4msQgPIeeZZgXUMg==</Encrypted>]]>
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