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
        Console.OutputEncoding = Encoding.UTF8;
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

            var hashKey = ReadLicense(secType, cmdType);
            if (string.IsNullOrEmpty(hashKey))
            {
                Console.Error.WriteLine("no-license");   // máy chưa có license Fast (%windir%\\<Máy>.c)
                return 2;
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
    /// Không có file / sai máy → rỗng.</summary>
    static string ReadLicense(Type secType, Type cmdType)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.MachineName + ".c");
            if (!File.Exists(path)) return "";
            var clientCode = (string?)cmdType.GetMethod("GetClientCode")!.Invoke(null, null) ?? "";
            var decrypt = secType.GetMethod("Decrypt", new[] { typeof(string), typeof(string) })!;
            var plain = (string?)decrypt.Invoke(null, new object?[] { File.ReadAllText(path), clientCode }) ?? "";
            var parts = plain.Split('ÿ');   // 'ÿ'
            // array[0] = tên máy, [1] = ngày tạo Windows, [2] = license. AnalysisSource tự kiểm lại đủ [0]/[1]; ở đây chỉ cần [2].
            if (parts.Length == 3 && string.Equals(parts[0], Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return parts[2];
        }
        catch { /* đọc license lỗi → coi như không có */ }
        return "";
    }

    static Assembly? Resolve(object? sender, ResolveEventArgs e)
    {
        var name = e.Name.Split(',')[0];
        var p = Path.Combine(_dir, name + ".dll");
        return File.Exists(p) ? Assembly.Load(File.ReadAllBytes(p)) : null;
    }
}
