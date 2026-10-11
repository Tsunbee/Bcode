using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Bcode.App.Models;

namespace Bcode.App.Services.Api;

/// <summary>
/// Hồ sơ dự án API lưu RIÊNG trên máy từng người: %AppData%\Bcode\ApiProjects\&lt;tên&gt;.json. Mật khẩu mã hoá DPAPI (CurrentUser) — chỉ tài khoản Windows
/// này trên máy này giải được; file chép sang máy khác thì phải nhập lại mật khẩu. Hồ sơ cũ (ApiProfiles, mật khẩu chữ thường) được đọc để chuyển sang.
/// </summary>
public static class ApiProjectStore
{
    public static string Folder => Path.Combine(BcodePaths.AppData, "Bcode", "ApiProjects");
    private static string OldFolder => Path.Combine(BcodePaths.AppData, "Bcode", "ApiProfiles");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static List<string> List()
    {
        try { Directory.CreateDirectory(Folder); return Directory.GetFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList(); }
        catch { return new(); }
    }

    public static ApiProject? Load(string name)
    {
        var p = PathOf(name);
        return File.Exists(p) ? JsonSerializer.Deserialize<ApiProject>(File.ReadAllText(p)) : null;
    }

    public static void Save(ApiProject project)
    {
        if (string.IsNullOrWhiteSpace(project.Name)) throw new InvalidOperationException("Chưa nhập tên dự án.");
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathOf(project.Name), JsonSerializer.Serialize(project, Opts), new UTF8Encoding(false));
    }

    public static void Delete(string name) { var p = PathOf(name); if (File.Exists(p)) File.Delete(p); }

    private static string PathOf(string name)
    {
        var safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Folder, safe + ".json");
    }

    /// <summary>Hồ sơ kiểu cũ (Khai báo API trước đây) chưa chuyển: tên + đường dẫn.</summary>
    public static List<string> OldProfiles()
    {
        try { return Directory.Exists(OldFolder) ? Directory.GetFiles(OldFolder, "*.json").ToList() : new(); }
        catch { return new(); }
    }

    /// <summary>Chuyển 1 hồ sơ cũ (ApiConfigItem: BaseApiUrl / TokenEndpoint / TokenRequestBody có mật khẩu chữ thường) sang dạng mới, mật khẩu mã hoá.</summary>
    public static ApiProject ConvertOld(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var p = new ApiProject
        {
            Name = S("ProjectId").Length > 0 ? S("ProjectId") : Path.GetFileNameWithoutExtension(path),
            ProjectId = S("ProjectId"),
            Department = S("Department"),
            BaseUrl = S("BaseApiUrl"),
            TokenPath = S("TokenEndpoint"),
            DocPath = S("SchemaFilePath"),
        };
        try
        {
            using var tb = JsonDocument.Parse(S("TokenRequestBody"));
            if (tb.RootElement.TryGetProperty("username", out var u)) p.Username = u.GetString() ?? "";
            if (tb.RootElement.TryGetProperty("password", out var pw)) p.PasswordProtected = Protect(pw.GetString() ?? "");
        }
        catch { /* body không phải JSON — để trống tài khoản */ }
        if (p.Forms.Count == 0) p.Forms.Add(new ApiForm { Name = "Call API", Kind = "custom", Endpoint = "", Body = "{}" });
        return p;
    }

    // ---------------------------------------------------------------- DPAPI (crypt32, CurrentUser)
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(Crypt(data, protect: true));
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try { return Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(protectedBase64), protect: false)); }
        catch { return ""; }   // mã hoá trên máy / tài khoản khác → coi như chưa nhập
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] Crypt(byte[] input, bool protect)
    {
        const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
        var inBlob = new DataBlob { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        var outBlob = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            var ok = protect
                ? CryptProtectData(ref inBlob, "Bcode API", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
