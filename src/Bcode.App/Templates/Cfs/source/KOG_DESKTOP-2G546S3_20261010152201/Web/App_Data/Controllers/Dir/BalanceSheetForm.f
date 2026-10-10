<?xml version="1.0" encoding="utf-8"?>

<!DOCTYPE dir [
	<!ENTITY ScriptIrregular SYSTEM "..\Include\Javascript\Irregular.txt">
	<!ENTITY CheckIrregular SYSTEM "..\Include\XML\CheckIrregular.txt">
	<!ENTITY defaultForm "v20GLTC1">
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
	<!ENTITY UpdateNormCompare "
if @ct_tong = 1 update @@table set ct_tong = 0 where form = @form and ts_nv = @ts_nv
">

	<!ENTITY % BalanceSheetName SYSTEM "..\Include\BalanceSheetName.ent">
	%BalanceSheetName;
]>

<dir table="v20gltc1" code="stt, ma_so, form" order="stt, ma_so, form" xmlns="urn:schemas-fast-com:data-dir">
	<title v="&Name.Dir.Title.v;" e="&Name.Dir.Title.e;"></title>
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
			<header v="Chỉ tiêu" e="Norm"></header>
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

		<field name="ts_nv" dataFormatString="1, 2" clientDefault="Default" defaultValue="1" align="right">
			<header v="Phân loại" e="Classify"></header>
			<footer v="1 - Tài sản, 2 - Nguồn vốn" e="1 - Asset, 2 - Capital"></footer>
			<items style="Mask"/>
		</field>
		<field name="ngoai_bang" dataFormatString="0, 1" clientDefault="0" align="right">
			<header v="Ngoại bảng" e="Off Balance Sheet"></header>
			<footer v="1 - Ngoại bảng, 0 - Trong bảng" e="1 - Off, 0 - On Balance Sheet Items"></footer>
			<items style="Mask"/>
			<clientScript><![CDATA[<Encrypted>iAnF8giecKm6/C7xJpljrVwL9krC0bAGPeGClE9fbiliMy937TiZ9gTvTA7KPGl7</Encrypted>]]></clientScript>
		</field>
		<field name="ct_tong" dataFormatString="0, 1" defaultValue="0" clientDefault="0" inactivate="true" align="right">
			<header v="Loại chỉ tiêu" e="Code Type"></header>
			<footer v="1 - Tổng cộng tài sản hoặc tổng cộng nguồn vốn, 0 - Không" e="1 - Summary of Assets and Liabilities, 0 - No"></footer>
			<items style="Mask"/>
		</field>
		
		<field name="kind" defaultValue="case when tk = '' then '0' else '1' end">
			<header v="Cách tính" e="Calculating Way"></header>
			<items style="DropDownList">
				<item value="0">
					<text v="0 - Tính theo mã số" e="0 - Base on Formula"/>
				</item>
				<item value="1">
					<text v="1 - Tính theo số dư" e="1 - Base on Balance"/>	
				</item>
				<item value="2">
					<text v="2 - Tính theo số phát sinh" e="2 - Base on Account Arising"/>	
				</item>
				<item value="3">
					<text v="3 - Số dư theo Khế ước" e="3 - Balance according to the contract"/>	
				</item>
				<item value="4">
					<text v="4 - Số dư theo công nợ hóa đơn phải thu" e="4 - Balance according to accounts receivable"/>	
				</item>
				<item value="5">
					<text v="5 - Số dư theo công nợ hóa đơn phải trả" e="5 - Balance according to accounts payable"/>	
				</item>
			</items>
			<clientScript><![CDATA[<Encrypted>iAnF8giecKm6/C7xJpljrVwL9krC0bAGPeGClE9fbiliMy937TiZ9gTvTA7KPGl7</Encrypted>]]></clientScript>
		</field>
		

		<field name="tk" clientDefault="Default" filterSource="Optional">
			<header v="Tài khoản" e="Account"></header>
			<items style="AutoComplete" controller="Account" reference="ten_tk%l" key="status = '1'" check="1 = 1" information="tk$dmtk.ten_tk%l" new="Default"/>
		</field>
		<field name="ten_tk%l" readOnly="true" external="true" defaultValue="''" clientDefault="Default">
			<header v="" e=""></header>
		</field>

		<field name="tk_du" clientDefault="Default">
			<header v="Tài khoản đối ứng" e="Reference Account"></header>
			<items style="AutoComplete" controller="Account" reference="ten_tk_du%l" key="status = '1'" check="1 = 1" information="tk$dmtk.ten_tk%l" new="Default"/>
		</field>
		<field name="ten_tk_du%l" readOnly="true" external="true" defaultValue="''" clientDefault="Default">
			<header v="" e=""></header>
		</field>
		
		<field name="so_ngay1" type="Decimal" dataFormatString="@quantityInputFormat" defaultValue="0">
			<header v="&lt;= Số ngày" e="&lt;= Annual Leave"></header>
			<items style="Numeric"/>
			<clientScript><![CDATA[<Encrypted>iAnF8giecKm6/C7xJpljrVwL9krC0bAGPeGClE9fbiliMy937TiZ9gTvTA7KPGl7</Encrypted>]]></clientScript>
		</field>
		<field name="so_ngay2" type="Decimal" dataFormatString="@quantityInputFormat" defaultValue="0">
			<header v="&gt;= Số ngày" e="&gt;= Annual Leave"></header>
			<items style="Numeric"/>
			<clientScript><![CDATA[<Encrypted>iAnF8giecKm6/C7xJpljrVwL9krC0bAGPeGClE9fbiliMy937TiZ9gTvTA7KPGl7</Encrypted>]]></clientScript>
		</field>
		
		
		<field name="cong_no" dataFormatString="0, 1" clientDefault="Default" defaultValue="0" align="right">
			<header v="Loại" e="Type"></header>
			<footer v="1 - Lấy chi tiết một vế của các đối tượng công nợ, 0 - Không" e="1 - AR/AP Items, 0 - No"></footer>
			<items style="Mask"/>
		</field>
		<field name="khong_am" dataFormatString="0, 1" clientDefault="0" align="right">
			<header v="Kiểu" e="Mode"></header>
			<footer v="1 - Lấy giá trị không âm, 0 - Không" e="1 - None Negative Values, 0 - No"></footer>
			<items style="Mask"/>
		</field>

		<field name="cach_tinh" clientDefault="Default" dataFormatString="X">
			<header v="Công thức" e="Formula"></header>
			<items style="Mask"/>
		</field>
	</fields>
	<views>
		<view id="Dir" height="172">
			<item value="8, 112, 40, 40, 20, 60, 30, 30, 210"/>
			<item value="1011----1: [stt_in].Label, [stt], [stt_in], [form]"/>
			<item value="101010---: [ma_so_in].Label, [ma_so], [ma_so_in]"/>
			<item value="101000000: [chi_tieu].Label, [chi_tieu]"/>
			<item value="101000000: [chi_tieu2].Label, [chi_tieu2]"/>

			<item value="101000000: [thuyet_minh].Label, [thuyet_minh]"/>
			<item value="100000000: [form].Description"/>
			<item value="101100000: [in_ck].Label, [in_ck], [in_ck].Description"/>
			<item value="101100000: [bold].Label, [bold], [bold].Description"/>
			<item value="100000000: [form].Description"/>

			<item value="101100000: [ts_nv].Label, [ts_nv], [ts_nv].Description"/>
			<item value="101100000: [ngoai_bang].Label, [ngoai_bang], [ngoai_bang].Description"/>
			<item value="101100000: [ct_tong].Label, [ct_tong], [ct_tong].Description"/>
			<item value="100000000: [form].Description"/>

			<item value="101000000: [kind].Label, [kind]"/>
			<item value="-11001000: [tk].Label, [tk], [ten_tk%l]"/>
			<item value="-11001000: [tk_du].Label, [tk_du], [ten_tk_du%l]"/>
			<item value="-1100----: [so_ngay1].Label, [so_ngay1]]"/>
			<item value="-1100----: [so_ngay2].Label, [so_ngay2]]"/>
			<item value="-11100000: [cong_no].Label, [cong_no], [cong_no].Description"/>
			<item value="-11100000: [khong_am].Label, [khong_am], [khong_am].Description"/>
			<item value="-11000000: [cach_tinh].Label, [cach_tinh]"/>
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
				<![CDATA[<Encrypted>SscxvCQrThdRRK/5ojOAw7Ei+4efIzCSDkRP82mfRB5tuz5xYsXF0rw90B4BTMQZGNmVR96byndU4rLY5qFdIhg/hbAF6G8y00/tVPsAbz8=</Encrypted>]]>&defaultForm;<![CDATA[<Encrypted>LvYIKSknyQil/ZQBLbfdJCNjZItXMMaOQEQD3VGHo+MWZjLIZAqCY6mC0/QwPGScOv5eEMzF5ZVflQf8ZJh6LCdJ4rf5iXiBq0ahjcvMBfp91QHBwmJCp094tkFIWia/BfPgLUR+n7drwajUGVR9hvAAuu/mmueKRzMgR3HDq8ij7hI1H6h/ProLtifTBpPzgQ0wZ8BdyUEaZL3dXH8+pG9rTUJ/jN6Ls4rNn9j1/pzBQyHVjctX3+K1LhgofjT/ASNal+BXmgIkQczsWbTvYbQfxznvPXXeZd2Fs90Gk8p1VOIrMRfe42nEiBhKRrBtL1jt/T2vPxoX0cU6ZDJGvLrTGhDwkukTYzai3o+4klbyzEqYcELJ2Qh0WMRaOmUPtFkZ5BFAoLdiLLNBBfpsTg==</Encrypted>]]>
				&Check;
				&UpdateNormCompare;
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
				&UpdateNormCompare;
			</text>
		</command>

		<command event="Updated">
			<text>
				&AfterUpdate;
			</text>
		</command>

		<command event="Deleting">
			<text>
				<![CDATA[<Encrypted>/vyQWCnb6YQxOe/6VlsHl7xjOVg5FdmB0m4cPAGc5BCieXcJdTGdipnUht8LqSChyKXtcsygK1wMLzD9AW4gQEUQdYToL/8msECjzLCoj0d4oPrh0RAX+gEg74oEaGnztIVpkFPkezAgN0AxipX3da4Nm/cPVvu/xHACgY4Hqlp62nxAkROFnhOwaLGAM72oTILnqTjJ70QPM5kfQUI0y8SN9t9ory0mKlaSbaZr3Zy9A34DzmKGFNHk5zOoulZzwRu2eBhog0DpyecOt6ImwNyMqaW7CD1qvZzBxAzMYp/cRgipOGXXQx0GqzUsKA8C6AGwuSgYtDsVCxmFVUgSzH63ZfAgzaiVGvjm9uRaPS47tKCQMJppPG9R2rlGWiLbwKqpSSmERufB+izdtR2r/fwOFlGDneIy18wyQRzaWewdwAlWqk4jmd01jgWSxtvSq1MJWHYt7318yDKd2yKvFA==</Encrypted>]]>
			</text>
		</command>

		&CheckIrregular;
	</commands>

	<script>
		<text>
			<![CDATA[<Encrypted>/n8nbiuh35YVoauXD1Fxi+euNVWYtugFkgdMCbqeRn2UsgwwTbzO904UMU7DvX0HWwcVHfmKhUGt16pWHhp8dMvSDiGM5nrYUbE4K2ZO/XPuwfqORODcuOmb5W807H8yhqSM26nsabQluDeP006voRxOdnLjSI7R6Iq11oy2EXBrzEFgYFsoM8ucR4fajxV01mfcKCw9UeCuJi6uZdPHFA91XnsHKXPajJsMLHd4loCGytxfs0SIBpDiSGRutu+1hIbkporJ+G2DDkSBuw7jyyJUrzTaito00OUUfQiM0VfEixP7cys8eL9UiiepGat2F+hQ/k5dGqRCIgy/h+on+9K3u1X2qTkVxTw0pV1yCkxd1sWAmhtmX0Z0epIioseZ071FkAdpVziLcmikHmwRKWCiJhOFF3Z/rDo5w0xpy6MtueS9xLuixWJuTk1IilCnjuQXM4cyXI/GVgb+K2sL13S4cqbhh4zXRixtpbWlscODyjQxhhiApuwr5Ddt1tDdbL770ZcMhbrrq8TJ3c1OhKIh4L9mKtx6U/iMsdr8sS3qB25limciKQtU9xVQ2CRA+Y1G7FnHkgMezSYWX1SeYE5RoSvzIMsB9PQvSqBRZ49DEk9+L975gG2+LVA+MjCVuUUvAxTg7yUt5S8yrjJytEbqpffXm+ZrRKoGykmm3TDauddsUYE4KTtcf9vFGpV5TokgvBShsHGrgADmaToIqKg6/E8rMJ2LGcSdULMOMMLfUo6h1iVVKnszJtqsGBldH9No20W2i4m9rIDMRdxR265q396U1m3DMajCwTj5F7QgrlCj0vUxkbF8IE9+xshu1ozrbZz/vPbs0LPfl60Czwe7yoW1+/NLGy7SmdJqU9TwD5edLuoris/JedYZwEEepEHakKrr1Z8HDW78hGBvNkBzpdFjOIP67+n1rFIR5F2NKCilKHNPQCGyHXEacvoJAhiw/DiYNQXvAk1XEf3jwtlAAJy+PGd0SO4kFJ+kyBEYaclK74SBCCDrMYE7AuAPFGUkmmB4nljv6MqXezrb3WxCNXho70pOINl/YgywnFe4SUX/wc6hB1j5zCjF1kzzLXP5NdGr2nj57H6aFRDWQynkl23gk3j6+6yxlzgE6pUayNjuFJl8iAYJ6Qmv42a3</Encrypted>]]>
			&ScriptIrregular;
		</text>
	</script>

	<css>
		<text>
			<![CDATA[<Encrypted>pAUjwHBdC/JYo28gWIu/LPYJD9ZGKGIeLk3PGob7Y7lVqxnU4KCBl9hnECVLtokEDjipQL6n8YKsynI4fZL9gtLYJMzZ6L54ByZsWMR6bwkPu9YfBklaselRZMHwDIOLxR469QSmaZsUAIfaBh52drerrcEBOqckyq8ZA/Cd7YlJ2jWe4Ggx99B9xtt60ii9QIV074uTHIO/aUMxs98l6iVLlKlKDrM0acoLMPFcfxo=</Encrypted>]]>
		</text>
	</css>
</dir>