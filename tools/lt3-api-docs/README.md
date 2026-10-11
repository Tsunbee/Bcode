# Bộ mẫu API chuẩn LT3

Sinh `src/Bcode.App/Templates/Api/lt3-standard.json` (mẫu "Khai báo API" của Bcode) từ tài liệu chuẩn của phòng LT3:
http://172.168.5.14/developers/docs/intro/

```
node fetch.mjs      # tải 20 trang tài liệu → pages/*.txt (văn bản + khối code)
node convert.mjs    # pages/*.txt → Templates/Api/lt3-standard.json
```

Nội dung bộ mẫu: SyncData (danh mục), SyncVoucher (chứng từ header/detail/tax), GetData — mỗi form có bảng trường (kiểu, bắt buộc, mô tả)
và request mẫu; mã lỗi chung; cách lấy token (`token.accesstoken`, `token.expires`, header `Authorization: <token>`).

Lưu ý tài liệu hiện tại: trang "Danh mục quy đổi đơn vị tính" liệt kê form `setUomConversion` nhưng request mẫu ghi `"form": "UnitConversion"` —
bộ mẫu giữ theo request mẫu.
