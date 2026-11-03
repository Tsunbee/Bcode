# SqlWatch

Ứng dụng Windows theo dõi câu lệnh SQL gần giống Profiler trong SSMS. Dùng khi cần xem ứng dụng vừa gửi câu gì lên SQL Server: batch, RPC, và stored procedure vừa bắt đầu.

Project: `C:\Users\THINHPM-LT3\source\SqlWatch`  
Chạy: `C:\Users\THINHPM-LT3\source\SqlWatch\bin\Debug\net9.0-windows\SqlWatch.exe`

```powershell
dotnet run --project C:\Users\THINHPM-LT3\source\SqlWatch
```

## Kết nối

User và password gắn cứng trong chương trình, không hiện trên màn hình. Login SQL là `profile`. Password không ghi trong file này.

Login này có `VIEW SERVER STATE`, không có quyền tạo session Profiler hay Extended Events. Vì vậy SqlWatch không mở trace trên server. Nó đọc DMV theo nhịp (mặc định 300 ms) và chỉ nhìn các database của project đang chọn.

Connection dùng `Application Name = SqlWatch` để không tự ghi lại câu lệnh của chính tool.

## Project và database

Danh sách lấy từ:

`%AppData%\Bcode\settings.json`

Mỗi workspace trong file đó cho một dòng project. Khi chọn project, app điền:

- Server của workspace
- Database: `SysDatabase`, `AppDatabase`, và các tên thêm trong `DbAccess` (tối đa 8, trùng thì bỏ)

Project mở sẵn là `LastWorkspace` trong settings. Ô Project gõ được để tìm trong danh sách. Server và database chỉ để xem, đổi project thì hai ô này đổi theo.

## Cách dùng

1. Chọn project.
2. Bấm **Bắt đầu**.
3. Làm thao tác trên ứng dụng cần xem.
4. Lưới hiện câu vừa chạy. Bấm một dòng để xem đủ nội dung ở khung **Chi tiết**. Double-click để copy.
5. **Tạm dừng** ngừng lấy câu mới. **Dừng** đóng kết nối. **Xóa lưới** xóa các dòng đã giữ.

Thanh trạng thái dạng `hiện số đang xem/tổng đã bắt`. Lưới giữ tối đa 2000 dòng mới nhất.

## Cột trên lưới

| Cột | Ý nghĩa |
|---|---|
| Thời điểm | Lúc request bắt đầu |
| Sự kiện | `SQL:BatchStarting`, `RPC:Starting`, hoặc `SP:Starting` |
| Nội dung | Câu SQL hoặc lệnh `EXEC` |
| Database | Database của session |
| SPID | Session id |
| Login | Login SQL |
| Máy | Máy client |
| Ứng dụng | `program_name` |
| Đối tượng | Tên stored procedure, nếu nhận ra |

Màu chữ: RPC xanh đậm, SP xanh lá, batch màu chữ thường.

## Sự kiện đang bắt

Nút **Sự kiện** là danh sách tick. Mặc định bật cả ba. Bỏ tick thì dòng loại đó ẩn khỏi lưới; tick lại thì hiện lại, kể cả dòng đã bắt từ trước.

| Sự kiện | Khi nào có dòng |
|---|---|
| `SQL:BatchStarting` | Client gửi một batch (câu lẻ, script, `EXEC ...` dạng text) |
| `RPC:Starting` | Client gọi kiểu RPC, thường là `CommandType.StoredProcedure`. Trên SQL 2008, procedure đang là module cuối của session cũng được ghi vào loại này |
| `SP:Starting` | Nhận ra tên stored procedure trong batch (`EXEC` / `EXECUTE`, kể cả `exec @rc = dbo.Proc`) hoặc trong RPC dạng `schema.proc;1` |

Một lần gọi procedure bằng `EXEC` thường ra hai dòng: batch chứa cả câu, và `SP:Starting` với tên procedure ở cột Đối tượng.

`sp_executesql` không được ghi thành một SP riêng. Tool đọc chuỗi bên trong và lấy `EXEC` procedure nằm trong đó. Các lệnh hệ thống sau cũng bị bỏ qua: `sp_prepare`, `sp_prepexec`, `sp_execute`, `sp_unprepare`, `sp_cursor*`, `sp_reset_connection`, `sp_getapplock`, `sp_releaseapplock`.

## Bộ lọc

Ba ô lọc áp lên **các dòng đang có trên lưới**, không chỉ dòng mới:

- **Login chứa**: cột Login
- **Ứng dụng chứa**: cột Ứng dụng
- **Nội dung chứa**: cột Nội dung và cột Đối tượng

Xóa chữ trong ô thì các dòng đó hiện lại. Đổi tick ở **Sự kiện** cũng lọc lại ngay.

## Đọc dữ liệu trên từng đời SQL Server

Nhịp đọc dùng `sys.dm_exec_sessions` và `sys.sysprocesses` (`dbid`) để biết database của session. Cột `database_id` của `sys.dm_exec_sessions` không có trên SQL Server 2008, nên không dùng cột đó.

Nội dung câu lệnh:

- Có `sys.dm_exec_input_buffer` (SQL 2014 SP2 / 2016 trở lên): đọc đúng buffer client, phân biệt Language Event và RPC Event.
- Không có hàm này (SQL Server 2008): đọc `sys.dm_exec_sql_text` qua `most_recent_sql_handle`.

Procedure còn đang chạy được đọc thêm từ `sys.dm_exec_requests` để bắt lần gọi lồng nhau không nằm sẵn trong text của batch.

## Giới hạn

- Không đo được `Duration`, `Reads`, `CPU`. Ba sự kiện trên chỉ cho biết câu nào vừa bắt đầu.
- Nhiều câu trên cùng một SPID hoàn tất trong một nhịp thì chỉ còn câu cuối trong buffer.
- Procedure chạy rất nhanh, không có tên trong text `EXEC`/`RPC`, có thể không thành dòng `SP:Starting`.
- Không ghi file trace trên server, không tự chạy lại khi SQL khởi động.
- Chỉ xem database của project đã chọn. Instance dùng chung nhiều khách hàng; không có ô “mọi database”.
