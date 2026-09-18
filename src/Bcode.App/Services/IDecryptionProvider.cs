namespace Bcode.App.Services;

/// <summary>
/// Pluggable hook for "Decrypt SQL Object". FCode's FDecryptSQLObject.exe uses an
/// encryption scheme that is proprietary to that product (see FastBusiness.Crypto.dll)
/// and unknown to Bcode — this app makes NO attempt to replicate or reverse it.
///
/// If your own SQL Server objects are encrypted WITH_ENCRYPTION using a scheme *you*
/// control (your own symmetric key / your own obfuscation before storing script text
/// in a config table, etc.), implement this interface with that logic and register it
/// in Program.cs. Left unimplemented, the Decrypt SQL Object tool just says so.
/// </summary>
public interface IDecryptionProvider
{
    string Name { get; }
    bool CanDecrypt(string cipherText);
    string Decrypt(string cipherText);
}

/// <summary>Default no-op provider — returns the input unchanged and reports it cannot decrypt anything.</summary>
public class PassthroughDecryptionProvider : IDecryptionProvider
{
    public string Name => "(chưa cấu hình thuật toán giải mã)";
    public bool CanDecrypt(string cipherText) => false;
    public string Decrypt(string cipherText) => cipherText;
}
