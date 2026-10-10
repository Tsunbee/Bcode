# Procedure bốc số LCTT gián tiếp của Fast — `rs_rptInDirectCashflow`

Nguồn: file procedure + lệnh chạy mẫu (form `V20GLTC605`, đơn vị 37) do người dùng cung cấp; bản gốc để nguyên ở `rs_rptInDirectCashflow.sql`.
Đọc procedure để biết **mỗi ô trên màn hình "Sửa báo cáo lưu chuyển tiền tệ theo phương pháp gián tiếp" (bảng `v20gltc6`) làm gì khi tính số**.

## Cột ↔ ô trên màn hình

| Cột `v20gltc6` | Ô trên màn hình | Ý nghĩa trong procedure |
|---|---|---|
| `kind` | Cách tính | 0 = công thức; 1 = số phát sinh; 2 = số dư |
| `cach_tinh` | Công thức | chỉ dùng khi kind = 0 |
| `tk` | Các tài khoản | lọc TK (khớp đầu chuỗi, nhiều TK ngăn bằng dấu phẩy) |
| `tk_du` | Các tài khoản đối ứng | lọc TK đối ứng — **chỉ dùng khi kind = 1** |
| `no_co` | Phân loại | 1 = Nợ, 2 = Có |
| `dau_cuoi` | Đầu/Cuối | 1 = đầu kỳ, 2 = cuối kỳ — **chỉ dùng khi kind = 2** |
| `khong_am` | Lấy giá trị không âm | **chỉ dùng khi kind = 2** |
| `cong_no` | Loại ("Lấy chi tiết một vế của các đối tượng công nợ") | **chỉ dùng khi kind = 2**, theo từng mã khách |
| `dau` | Thu/Chi | 1 = Thu (giữ dấu); 0 = Chi → **đổi dấu cuối cùng (số âm)** |
| `type` | Kiểu phát sinh | 1 = chỉ tính phát sinh của kỳ cuối (tháng cuối), 0 = cả kỳ — chỉ kind = 1 |
| `in_ck`, `bold` | In / Kiểu chữ | chỉ để hiển thị |

## Cách tính từng loại

* **kind = 0 (công thức)**: không vào vòng lặp tính số. Sau khi tính xong các dòng kind ≠ 0 mới cộng theo `cach_tinh` (`ConvertFormula`, theo thứ tự `ids`). Các ô khác của dòng này **không có tác dụng**.
* **kind = 1 (số phát sinh)**: từ bảng phát sinh `#ct00` (nhóm theo TK + TK đối ứng, lấy từ `pstkdu$…` + `r00$…` của tháng chưa khoá sổ):
  * Phân loại = Nợ → cộng `ps_no`; Có → cộng `ps_co`, **của TK ở ô "Các tài khoản"**.
  * Điều kiện: TK khớp `tk` **và** TK đối ứng khớp `tk_du`. Ô nào để trống thì **không lọc** (vd chỉ tiêu 06: tk 635, tk_du trống → lấy mọi phát sinh Nợ 635 bất kể đối ứng).
  * Bỏ qua `dau_cuoi`, `khong_am`, `cong_no`.
* **kind = 2 (số dư)**: lấy số dư theo **từng nhóm TK khai báo** (mỗi TK/tiền tố trong ô "Các tài khoản" là một nhóm — nên Fast hay tách `1311` / `1312` ra từng nhóm để cấn trừ riêng):
  * Dư Nợ nhóm = max(Nợ − Có, 0), dư Có nhóm = max(Có − Nợ, 0).
  * `cong_no = 0`: Nợ → `du_no − du_co` (tức Nợ − Có theo nhóm, có thể âm); Có → `du_co − du_no`; `khong_am = 1` thì chặn dưới 0 theo từng nhóm.
  * `cong_no = 1`: tách theo **từng mã khách** trong nhóm, mỗi khách chỉ lấy một vế (Nợ → tổng dư Nợ các khách, Có → tổng dư Có), `khong_am` không còn tác dụng. Chỉ có nghĩa với TK công nợ (131, 331, 1388, 3388…).
  * `dau_cuoi = 1` → số dư đầu kỳ (`#BgAccts/#BgCusts`), `2` → cuối kỳ (`#EdAccts/#EdCusts`).
  * Bỏ qua `tk_du`.
* **Dấu cuối cùng**: sau khi tính, các dòng `dau = 0` (Chi) bị đổi dấu → chi ra là số âm. Các dòng công thức cộng trừ trên số đã đổi dấu.
* Cột kỳ: kỳ này / kỳ trước (và luỹ kế khi mẫu giữa niên độ), có cả bản ngoại tệ (`_nt`).
