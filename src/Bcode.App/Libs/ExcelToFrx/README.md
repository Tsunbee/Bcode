# Libs/ExcelToFrx/

`ExcelToFrx.dll`: màn hình "Excel mẫu in → FastReport (.frx)" (tab **Excel → FRX**).
Source: `D:\phongnt\ConvertBcode`. Đó là bản clone của `D:\phongnt\Convert\tool`, đã port sang C#
và không cần Python. Xem `docs\GAN_VAO_BCODE.md` trong repo đó.

- `FrxGenerator\`: FrxGenerator.exe (net48) + FastReport.dll của công ty. Csproj chép ra
  <output>\ExcelToFrx\FrxGenerator\; MainForm.OpenExcelToFrxTab trỏ FrxGeneratorPath tới đó. FastReport là bản .NET Framework, không nạp được vào Bcode (.NET 8),
  nên DLL gọi exe này bằng tiến trình con.
- `Templates\Frx\`: 28 mẫu .frx chuẩn (bộ FRX_Demo), chép ra <output>\ExcelToFrx\Templates\Frx\. Mẫu Excel trùng tên thì tự lấy làm mẫu.

Cập nhật: build ConvertBcode ở cấu hình Release rồi chép đè lại ba thứ trên.
