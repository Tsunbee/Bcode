# Source khai báo mẫu báo cáo của Fast (template source gốc)

`KOG_DESKTOP-2G546S3_20261010152201\Web\App_Data\Controllers\{Dir,Filter,Grid}\{BalanceSheetForm,CashFlowD,CashFlowID}.f`
— màn hình khai báo mẫu báo cáo: **BalanceSheetForm** = CĐKT (bảng v20gltc1), **CashFlowD** = LCTT trực tiếp (v20GLTC5), **CashFlowID** = LCTT gián tiếp (v20GLTC6).

- `Dir` = màn hình khai báo từng chỉ tiêu (các trường: stt, ma_so, chi_tieu, tk, tk_du/tk_no/tk_co, no_co, dau_cuoi, cong_no, khong_am, kind, type…); `Filter` = chọn mẫu (form) theo bảng danh mục v20dmmaubc; `Grid` = lưới xem.
- Các câu lệnh (`<command>`, `<script>`, `<clientScript>`) trong file là mã hoá của Fast (`<Encrypted>`) — giữ nguyên, KHÔNG giải mã.
- Ý nghĩa cột đã học từ phần không mã hoá: xem `Services\CfsFast.cs` (FastLine) và bộ khai báo thật ở `..\fast\*.json`.
