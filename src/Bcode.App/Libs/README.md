# Libs/

`FastBusiness.Crypto.dll` here is Bee's own FCode client library, copied in unmodified
from his FCode installation (`D:\Tool\FCode\FCode\FastBusiness.Crypto.dll`).

**How it's used — and how it's not:**

- Bcode references it as an ordinary .NET library and calls only its *public* static API
  (`Crypto.RSADecrypt(...)`, `Crypto.Encode(...)`, etc.) — exactly the same way FCode.exe
  itself calls it. This is no different from depending on any other library.
- It is **not** decompiled, unpacked, patched, or otherwise reverse engineered. An earlier
  attempt to decompile it (to recover the algorithm as portable C#) hit a commercial
  obfuscator/protector that strips real method bodies from the IL — that attempt was
  abandoned specifically because getting past that protection would mean defeating an
  anti-tampering layer, which is a different (and declined) kind of task from calling a
  library's own public methods.
- Used for exactly one purpose: decrypting Bee's own `ConnectStr` registry value
  (`HKCU\SOFTWARE\FCoder\ConnectStr`) for the `Ctrl+F5` "Mã dự án" quick-connect feature.
  See `MainForm.DebugDecryptConnectStr` for the first diagnostic pass (verifying which
  public method — if any — produces a sane result under .NET 8, since this DLL predates it).

Do not add more files here without the same reasoning: reference + call public API only.
