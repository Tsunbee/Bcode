# Bcode — hướng dẫn cho Claude

- Trước khi tìm/sửa code: đọc `STRUCTURE.md` (bản đồ tính năng → file, công thức thêm tool, bẫy). Cây thư mục chi tiết ở `STRUCTURE-TREE.md`. Tài liệu Word kèm sơ đồ: `Cấu trúc Bcode  BcodeViewer (v3 - Tóm tắt).docx` (tra nhanh) và `... (v3 - Đầy đủ).docx` (cho người khác đọc, có mô tả từng file).
- Chỉ mở rộng đọc code khi bản đồ không đủ. Bỏ qua `src/BcodeViewer.App/Web/vs/` (Monaco vendor) và `src/Bcode.App/Templates/fileSource/` (hàng nghìn file mẫu).
- Sau khi thêm/xoá/đổi chức năng lớn: sửa `_docwork/content.py` (bảng tính năng) rồi chạy `python _docwork/build.py` (bản tóm tắt + STRUCTURE*.md) và `FULL=1 python _docwork/build.py` (bản đầy đủ) (sơ đồ: `python _docwork/diagrams.py`).
- Script có đường dẫn Windows (dấu \) phải ghi bằng Write, không dùng heredoc trong Bash.
