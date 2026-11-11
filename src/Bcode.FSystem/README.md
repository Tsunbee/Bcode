# Bcode.FSystem — engine giải mã khối `<Encrypted>` của file .f (Fast)

DLL sạch, viết lại từ nguồn `fsystem` của FCode. **Tự chứa**: không reflection, không cần `fsystem.dll` cài sẵn, không đọc license `%windir%\<máy>.c`. Khoá (`hashKey` 391 ký tự) do nơi gọi truyền vào.

## File
- `FEngine.cs` — engine thật. **Nằm trong `.gitignore`** (không đẩy logic giải mã lên repo công khai). Chỉ có trên máy build của người phát hành.
- `FEngine.Empty.cs` — bản rỗng, **được commit**. Máy không có `FEngine.cs` build bằng file này: `Decode` trả nguyên văn, `Available = false`.
- `Bcode.FSystem.crproj` — cấu hình ConfuserEx (obfuscate), chỉ chạy khi build **Release**.

## API
```csharp
Bcode.FSystem.FEngine.Available            // có engine thật không
Bcode.FSystem.FEngine.Decode(src, hashKey, beauty=false)   // giải mọi khối <Encrypted> trong src
```

## Khoá
`hashKey` = `array[2]` của license Fast (hằng số toàn cục). Bcode để ở `src/Bcode.App/Secrets/LocalSecrets.cs` (`FHashKey`, gitignore) và truyền cho `Bcode.FDecrypt.exe` qua **stdin**. Chi tiết thuật toán: xem memory `fcode-f-decrypt`.

## Chép FEngine.cs sang máy build khác
Qua kênh an toàn (như `Secrets/LocalSecrets.cs`). Thiếu file → tính năng dịch .f tắt, Bcode vẫn chạy bình thường.
