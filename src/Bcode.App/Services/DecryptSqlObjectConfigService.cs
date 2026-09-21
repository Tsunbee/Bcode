using System.Text;

namespace Bcode.App.Services;

/// <summary>
/// Reads FCode's own "Decrypt SQL Object" settings file (descryptsqlobject.config, normally
/// next to FCode.exe). This file is NOT encrypted — it's a small, plain UTF-16LE text file:
/// FCode writes each field as UTF-16LE text and uses a few non-printable Unicode characters
/// (U+00FF, U+00FE, U+0100) as field separators instead of real .ini/.xml/.json syntax. There
/// is no cryptography involved at all, so reading it is unrelated to the "Về Decrypt SQL
/// Object" policy (see IDecryptionProvider's and DecryptSqlObjectForm's doc comments) — that
/// policy is about NOT reverse-engineering the algorithm FCode uses to decrypt a WITH
/// ENCRYPTION object's actual BODY. This class just reads a plain-text settings file, the same
/// spirit as FCodeConfigImportService reading Config.xml.
///
/// Layout observed in Bee's real file, splitting the UTF-16LE text (after its BOM) on
/// U+00FF / U+00FE / U+0100:
///   [0] a numeric id ("36007" in Bee's file — build/customer number, unclear which)
///   [1] app name ("fcode")
///   [2] server\instance ("lt3_phuocnd\sql2008")
///   [3] SQL login
///   [4] SQL password
///   [5], [7], [9], ... pairs of (App database name, version) — each remembered sample
///       database FCode last tested "Decrypt SQL Object" against, e.g.
///       ("VIETMONEY_SP2264_A", "22.6.4"), ("JTEMH_SP2262_A", "22.6.2").
///
/// NOT wired into anything automatically. [2]-[4] look like FastBusiness's own internal
/// dev/test SQL login (server name "lt3_phuocnd" — a developer machine — not one of Bee's real
/// customer servers from Config.xml), and DecryptSqlObjectForm's UI is "paste ciphertext,
/// click Decrypt" — it has no server-connection step to auto-fill these into yet. Exposed here
/// as plain data so Bee can inspect the parsed fields and decide what, if anything, to build
/// on top (e.g. a "connect + list Decrypt SQL Object targets" step in that form).
/// </summary>
public static class DecryptSqlObjectConfigService
{
    public record ParsedEntry(string AppDatabase, string Version);

    public record ParsedConfig(
        string Id, string AppName, string Server, string User, string Password,
        List<ParsedEntry> Entries);

    public static ParsedConfig Load(string configPath)
    {
        var bytes = File.ReadAllBytes(configPath);
        var text = Encoding.Unicode.GetString(bytes).TrimStart('﻿');

        var fields = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch is 'ÿ' or 'þ' or 'Ā')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) fields.Add(current.ToString());

        string At(int i) => i < fields.Count ? fields[i] : "";

        var entries = new List<ParsedEntry>();
        for (var i = 5; i + 1 < fields.Count; i += 2)
            entries.Add(new ParsedEntry(At(i), At(i + 1)));

        return new ParsedConfig(
            Id: At(0), AppName: At(1), Server: At(2), User: At(3), Password: At(4),
            Entries: entries);
    }
}
