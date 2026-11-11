using System;
using System.IO;
using System.Reflection;
using System.Text;

// Bcode.FDecrypt — giải mã file controller .f của Fast (khối <Encrypted>) bằng fsystem.dll ĐÃ CÀI của FCode.
//
// Dùng:  Bcode.FDecrypt.exe <thư-mục-fsystem> <file.f>
//   <thư-mục-fsystem> : thư mục chứa fsystem.dll + các dll của FCode (thường là nơi cài FCode).
//   <file.f>          : file controller cần giải.
// Ra:   stdout = nội dung đã giải (UTF-8).  stderr = chẩn đoán.
//   exit 0 = đã giải (có thể vẫn nguyên nếu không có gì để giải)
//   exit 2 = máy KHÔNG có license Fast → không giải được (Bcode quay về dò source chuẩn)
//   exit 3 = lỗi (thiếu fsystem.dll, file không đọc được…)
//
// Vì sao tách tiến trình: fsystem.dll bị làm rối và kéo theo nhiều phụ thuộc; nạp thẳng vào Bcode dễ gây sự cố cả app.
// Vì sao không kèm khoá: khoá giải là chuỗi license Fast ghi theo máy ở %windir%\<Máy>.c (giải bằng mã ổ cứng qua
// Cmd.GetClientCode) — chỉ máy dev được Fast cấp license mới đọc được. Repo public không chứa khoá nào.

static class Program
{
    static string _dir = "";

    static int Main(string[] args)
    {
        // UTF-8 KHÔNG BOM: .NET Framework ghi BOM ra stdout bị chuyển hướng nếu dùng Encoding.UTF8 → đầu nội dung dính ﻿.
        Console.OutputEncoding = new UTF8Encoding(false);
        // Chẩn đoán license: Bcode.FDecrypt.exe --diag <thư-mục-fsystem> → in tên máy, 32/64-bit, ngày cài Windows fsystem thấy, có file .c không.
        if (args.Length == 2 && args[0] == "--diag") return Diag(args[1]);
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Dùng: Bcode.FDecrypt.exe <thư-mục-fsystem> <file.f>");
            return 3;
        }
        _dir = args[0];
        var file = args[1];
        var fsystem = Path.Combine(_dir, "fsystem.dll");
        if (!File.Exists(fsystem)) { Console.Error.WriteLine("Không thấy fsystem.dll ở " + _dir); return 3; }
        if (!File.Exists(file)) { Console.Error.WriteLine("Không thấy file " + file); return 3; }

        string content;
        try { content = File.ReadAllText(file); }
        catch (Exception ex) { Console.Error.WriteLine("Không đọc được file: " + ex.Message); return 3; }

        // Chưa có <Encrypted> thì khỏi cần fsystem — trả nguyên văn.
        if (content.IndexOf("<Encrypted>", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Console.Out.Write(content);
            return 0;
        }

        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            var asm = Assembly.Load(File.ReadAllBytes(fsystem));
            var secType = asm.GetType("fsystem.Sec") ?? throw new Exception("fsystem.dll không có lớp Sec");
            var cmdType = asm.GetType("fsystem.Cmd") ?? throw new Exception("fsystem.dll không có lớp Cmd");

            var hashKey = ReadLicense(secType, cmdType, out var why);
            if (string.IsNullOrEmpty(hashKey))
            {
                Console.Error.WriteLine(why);   // "no-license" = chưa có / không đọc được %windir%\<Máy>.c; khác = license không khớp máy
                return why == "no-license" ? 2 : 4;
            }

            var analysis = secType.GetMethod("AnalysisSource") ?? throw new Exception("fsystem.Sec không có AnalysisSource");
            // beauty_yn = false: giữ nguyên code, không format lại (sơ đồ chỉ cần dò tên object)
            var outText = (string?)analysis.Invoke(null, new object?[] { content, hashKey, false }) ?? "";
            Console.Out.Write(outText);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine((ex.InnerException ?? ex).Message);
            return 3;
        }
    }

    /// <summary>Lấy chuỗi license (hashKey) đúng cách FCode làm: %windir%\&lt;Máy&gt;.c → Sec.Decrypt(…, Cmd.GetClientCode()) → tách 'ÿ', phần tử [2].
    /// Kiểm đủ như Sec.AnalysisSource: [0] = tên máy, [1] = ngày tạo Windows (Cmd.GetCreatedDateWindow, yyyyMMddHHmmss) — lệch thì AnalysisSource
    /// trả nguyên văn, nên báo lý do ngay ở đây. Không có file → <paramref name="why"/> = "no-license".</summary>
    static string ReadLicense(Type secType, Type cmdType, out string why)
    {
        why = "no-license";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.MachineName + ".c");
        if (!File.Exists(path)) return "";
        try
        {
            var clientCode = (string?)cmdType.GetMethod("GetClientCode")!.Invoke(null, null) ?? "";
            var decrypt = secType.GetMethod("Decrypt", new[] { typeof(string), typeof(string) })!;
            var plain = (string?)decrypt.Invoke(null, new object?[] { File.ReadAllText(path), clientCode }) ?? "";
            var parts = plain.Split('ÿ');   // 'ÿ'
            if (parts.Length != 3) { why = "license-mismatch: " + path + " không giải được bằng mã ổ cứng của máy này (license cấp cho máy khác?)"; return ""; }
            if (!string.Equals(parts[0], Environment.MachineName, StringComparison.Ordinal))
            { why = "license-mismatch: license cấp cho máy '" + parts[0] + "', máy này là '" + Environment.MachineName + "'"; return ""; }
            var created = cmdType.GetMethod("GetCreatedDateWindow")?.Invoke(null, null) is DateTime d ? d.ToString("yyyyMMddHHmmss") : null;
            if (created is not null && parts[1] != created)
            { why = "license-mismatch: license ghi ngày cài Windows " + parts[1] + ", máy này là " + created + " (cài lại / nâng cấp Windows sau khi cấp license?)"; return ""; }
            return parts[2];
        }
        catch (Exception ex) { why = "license-mismatch: đọc license lỗi — " + (ex.InnerException ?? ex).Message; return ""; }
    }

    static int Diag(string dir)
    {
        _dir = dir;
        var lic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.MachineName + ".c");
        Console.WriteLine("Máy: " + Environment.MachineName + " · tiến trình " + (Environment.Is64BitProcess ? "64" : "32") + "-bit");
        Console.WriteLine("License: " + lic + (File.Exists(lic) ? " (có)" : " (KHÔNG có)"));
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            var asm = Assembly.Load(File.ReadAllBytes(Path.Combine(dir, "fsystem.dll")));
            var cmdType = asm.GetType("fsystem.Cmd")!;
            var created = cmdType.GetMethod("GetCreatedDateWindow")?.Invoke(null, null) is DateTime d ? d.ToString("yyyyMMddHHmmss") : "?";
            Console.WriteLine("Ngày cài Windows (fsystem thấy): " + created);
            if (File.Exists(lic))
            {
                var key = ReadLicense(asm.GetType("fsystem.Sec")!, cmdType, out var why);
                Console.WriteLine(string.IsNullOrEmpty(key) ? "License: " + why : "License: khớp máy này — giải mã được.");
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("Lỗi: " + (ex.InnerException ?? ex).Message); return 3; }
    }

    static Assembly? Resolve(object? sender, ResolveEventArgs e)
    {
        var name = e.Name.Split(',')[0];
        var p = Path.Combine(_dir, name + ".dll");
        return File.Exists(p) ? Assembly.Load(File.ReadAllBytes(p)) : null;
    }
}
