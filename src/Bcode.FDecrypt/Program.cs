using System;
using System.IO;
using System.Text;
using Bcode.FSystem;

// Bcode.FDecrypt — giải mã file controller .f của Fast (khối <Encrypted>) bằng engine SẠCH Bcode.FSystem.dll.
//
// KHÁC bản cũ: KHÔNG nạp fsystem.dll cài sẵn, KHÔNG đọc license %windir%\<máy>.c. Engine tự chứa, khoá (hashKey 391 ký tự)
// do Bcode truyền vào qua STDIN (không hiện ở dòng lệnh). Chạy tiến trình tách để lỗi/treo không làm sập Bcode và để giữ
// nguyên giao thức với Sơ đồ phụ thuộc (FSourceDecryptService gọi exe này).
//
// Dùng:  (Bcode) ghi hashKey vào stdin  →  Bcode.FDecrypt.exe <file.f>
// Ra:   stdout = nội dung đã giải (UTF-8, không BOM).  stderr = chẩn đoán.
//   exit 0 = xong (có thể nguyên văn nếu không có gì để giải)
//   exit 3 = lỗi (không đọc được file…)
//   exit 5 = thiếu hashKey (stdin rỗng)
//   exit 6 = bản build không có engine thật (chỉ FEngine.Empty.cs)

internal static class Program
{
    private static int Main(string[] args)
    {
        // UTF-8 KHÔNG BOM: .NET Framework ghi BOM ra stdout bị chuyển hướng nếu dùng Encoding.UTF8 → đầu nội dung dính ﻿.
        Console.OutputEncoding = new UTF8Encoding(false);

        if (args.Length < 1)
        {
            Console.Error.WriteLine("Dùng: (ghi hashKey vào stdin) Bcode.FDecrypt.exe <file.f>");
            return 3;
        }
        var file = args[0];
        if (!File.Exists(file)) { Console.Error.WriteLine("Không thấy file " + file); return 3; }

        string content;
        try { content = File.ReadAllText(file); }
        catch (Exception ex) { Console.Error.WriteLine("Không đọc được file: " + ex.Message); return 3; }

        // Chưa có <Encrypted> thì khỏi cần khoá — trả nguyên văn.
        if (content.IndexOf("<Encrypted>", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Console.Out.Write(content);
            return 0;
        }

        if (!FEngine.Available)
        {
            Console.Error.WriteLine("engine-missing: bản build này không có engine giải mã (chỉ FEngine.Empty.cs).");
            return 6;
        }

        // hashKey đọc từ stdin (Bcode ghi vào) — không để lộ ở dòng lệnh / danh sách tiến trình.
        string hashKey;
        try { hashKey = Console.In.ReadToEnd().Trim(); }
        catch (Exception ex) { Console.Error.WriteLine("Không đọc được hashKey từ stdin: " + ex.Message); return 3; }
        if (string.IsNullOrEmpty(hashKey)) { Console.Error.WriteLine("no-key: chưa có hashKey (stdin rỗng)."); return 5; }

        try
        {
            // beauty=false: giữ nguyên code, không format lại (Sơ đồ phụ thuộc chỉ cần dò tên object).
            var outText = FEngine.Decode(content, hashKey, false);
            Console.Out.Write(outText);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine((ex.InnerException ?? ex).Message);
            return 3;
        }
    }
}
