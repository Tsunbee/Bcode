# Plugin cho Bcode (mục 4 — "cành mọc thêm")

Thêm công cụ vào Bcode mà **không cần build lại**: bỏ một file khai JSON vào một trong hai thư mục:

- `<thư mục Bcode.exe>\Plugins\*.json` — đi theo bản phát hành.
- `%AppData%\Bcode\plugins\*.json` — riêng từng máy.

Mỗi plugin thành một mục trong menu **Tool mở rộng** và trong **Ctrl+P**. Plugin **chỉ chạy khi bạn bấm**, không tự chạy gì lúc Bcode mở.

> Mức an toàn: plugin **không** nạp DLL vào trong Bcode (tránh plugin lỗi làm treo Bcode hoặc mã lạ chạy trong tiến trình). Nó chỉ **gọi một chương trình ngoài** hoặc **mở một URL/đường dẫn**.

## Định dạng

```json
{
  "id": "mytool",
  "name": "Mở công cụ X",
  "type": "exe",
  "target": "tools\\x.exe",
  "args": "--fast",
  "cwd": "",
  "shortcut": "Ctrl+Shift+1"
}
```

| Trường | Bắt buộc | Ý nghĩa |
|---|---|---|
| `id` | không | Mã duy nhất (mặc định = tên file). Trùng id thì file gặp trước thắng. |
| `name` | **có** | Chữ hiện trên menu / Ctrl+P. |
| `type` | không | `exe` (mặc định) chạy chương trình; `url` mở link/đường dẫn bằng ứng dụng mặc định. |
| `target` | **có** | `exe`: đường dẫn chương trình. Tuyệt đối, hoặc tên lệnh trơn (`notepad.exe` → tìm theo PATH), hoặc tương đối (`tools\x.exe` → tính từ thư mục chứa file khai). `url`: đường dẫn/URL. |
| `args` | không | Tham số dòng lệnh (chỉ `exe`). |
| `cwd` | không | Thư mục làm việc (mặc định = thư mục chứa file khai). |
| `shortcut` | không | Phím tắt, vd `Ctrl+Shift+1`. |

File khai hỏng hoặc thiếu `name`/`target` sẽ bị bỏ qua, không ảnh hưởng Bcode.
