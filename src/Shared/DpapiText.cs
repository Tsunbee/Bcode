using System.Runtime.InteropServices;
using System.Text;

namespace Bcode.Shared;

/// <summary>
/// Mã hoá chuỗi bí mật (mật khẩu SQL, mật khẩu web, API key) khi ghi xuống file JSON bằng DPAPI CurrentUser — chỉ đúng tài khoản Windows
/// trên đúng máy đã mã hoá mới giải được. Dạng lưu: <c>"dpapi:" + base64</c>; chuỗi không có tiền tố là dữ liệu cũ dạng chữ thường (đọc
/// được như cũ, lần ghi sau tự mã hoá) — nên settings.json cũ không phải sửa tay, bản Bcode/Viewer cũ ghi lại file cũng không mất gì.
/// File LINK (không chép) vào Bcode.App và BcodeViewer.App — Viewer đọc chung settings.json của Bcode.
/// </summary>
internal static class DpapiText
{
    public const string Prefix = "dpapi:";

    public static bool IsProtected(string? stored) => stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        return Prefix + Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(plain), protect: true));
    }

    /// <summary>Giải chuỗi đã lưu. Chuỗi cũ (không tiền tố) trả nguyên. <paramref name="failed"/> = true khi chuỗi được mã hoá trên máy /
    /// tài khoản Windows khác (chép settings.json sang máy khác) — khi đó trả "".</summary>
    public static string Unprotect(string? stored, out bool failed)
    {
        failed = false;
        if (string.IsNullOrEmpty(stored)) return "";
        if (!IsProtected(stored)) return stored;
        try { return Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(stored.Substring(Prefix.Length)), protect: false)); }
        catch { failed = true; return ""; }
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
        var inBlob = new DataBlob { cbData = input.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, input.Length)) };
        var outBlob = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            var ok = protect
                ? CryptProtectData(ref inBlob, "Bcode", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
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

/// <summary>
/// Một trường bí mật của lớp settings: giữ bản đã lưu (chuỗi trong JSON) và bản chữ thường, chuyển đổi LƯỜI — đọc settings.json có
/// hàng trăm workspace không phải giải từng mật khẩu, ghi lại không phải mã hoá lại mật khẩu không đổi (DPAPI cho ra chuỗi khác mỗi
/// lần, mã hoá lại sẽ làm file đổi dù không ai sửa gì). Cách dùng trong lớp được serialize bằng System.Text.Json:
/// <code>
/// private readonly ProtectedText _password = new();
/// [JsonIgnore] public string Password { get => _password.Plain; set => _password.Plain = value; }
/// [JsonPropertyName("Password")] public string PasswordStored { get => _password.Stored; set => _password.Stored = value; }
/// </code>
/// Tên trong JSON giữ nguyên ("Password") để bản cũ vẫn đọc / ghi được file.
/// </summary>
internal sealed class ProtectedText
{
    private string _stored = "";
    private string? _plain;
    private bool _dirty;
    private bool _unreadable;

    /// <summary>Giá trị thật (đã giải mã). Rỗng khi chưa nhập hoặc khi giải không được (xem <see cref="Unreadable"/>).</summary>
    public string Plain
    {
        get
        {
            if (_plain is null)
            {
                _plain = DpapiText.Unprotect(_stored, out var failed);
                _unreadable = failed;
            }
            return _plain;
        }
        set
        {
            value ??= "";
            if (value == Plain) return;   // gán lại đúng giá trị (vd ô mật khẩu trống của dòng không giải được) → giữ bản đã lưu
            _plain = value;
            _dirty = true;
            _unreadable = false;
        }
    }

    /// <summary>Chuỗi ghi vào / đọc từ JSON.</summary>
    public string Stored
    {
        get
        {
            if (_dirty || (_stored.Length > 0 && !DpapiText.IsProtected(_stored)))
            {
                _stored = DpapiText.Protect(_plain ?? _stored);
                _dirty = false;
            }
            return _stored;
        }
        set
        {
            _stored = value ?? "";
            _plain = null;
            _dirty = false;
            _unreadable = false;
        }
    }

    /// <summary>true = giá trị được mã hoá bằng tài khoản Windows / máy khác (settings.json chép từ nơi khác) — phải nhập lại.</summary>
    public bool Unreadable { get { _ = Plain; return _unreadable; } }
}
