# RptGenerator (bản build sẵn cho tool Excel → RPT)

`RptGenerator.exe` là bước 2 của tool Excel → RPT: đọc `layout.json` (Bcode sinh ở
`Services/ExcelToRpt`) và dùng SDK Crystal Reports để tạo file `.rpt` (+ PDF xem trước).

- Mã nguồn: `D:\phongnt\Convert\tool\RptGenerator` (net48, **x86** — đừng build lại thành
  AnyCPU/x64, DLL Crystal chỉ có bản 32-bit).
- Bcode (.NET 8 x64) không nạp được DLL Crystal nên gọi exe này như tiến trình phụ
  (`RptGeneratorRunner`). `Bcode.App.csproj` chép thư mục này ra `<output>\tools\RptGenerator`.
- Chỉ gồm `RptGenerator.exe`, `RptGenerator.exe.config`, `Newtonsoft.Json.dll`. **Không** chép
  `CrystalDecisions.*.dll` vào đây: chúng phải đến từ Crystal Reports runtime đã cài trên máy
  (GAC) — copy lẻ dễ lệch phiên bản. Máy chưa cài: *SAP Crystal Reports runtime 32-bit
  (CRRuntime_32bit)*. Không có runtime thì tab Excel → RPT vẫn mở và chỉnh bố cục được, chỉ
  "Xem trước PDF" / "Tạo file .rpt" báo lỗi.

Cập nhật khi mã nguồn RptGenerator đổi:

```
cd D:\phongnt\Convert\tool
dotnet build RptGenerator\RptGenerator.csproj -c Release
copy RptGenerator\bin\Release\net48\RptGenerator.exe*    <Bcode>\tools\RptGenerator\
copy RptGenerator\bin\Release\net48\Newtonsoft.Json.dll  <Bcode>\tools\RptGenerator\
```

Dùng bản exe khác thì khai `RptGeneratorExePath` trong `%AppData%\Bcode\settings.json`.
