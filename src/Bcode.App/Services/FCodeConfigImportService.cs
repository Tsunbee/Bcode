using System.Xml.Linq;

namespace Bcode.App.Services;

/// <summary>
/// Reads FCode's OWN local Config.xml (the file FCode itself already keeps next to
/// FCode.exe, at Config\Config.xml — a plain, already-decrypted list of every project FCode
/// knows about: server, Sys/App database, paths, WLoginLink...) so Bcode's "Ctrl+F5
/// synchronize" (MainForm.QuickSelectProjectByCode) can look up the REAL per-project server
/// instead of guessing one fixed default — per Bee: "sai rồi, vì có những dự án có ở sql2014,
/// sql2016" (some projects really are on SQL2014/2016, not always SQL2008).
///
/// This is deliberately NOT the same thing as FCode's actual remote project-lookup service —
/// the one behind HKCU\SOFTWARE\FCoder\ConnectStr, reached only after decrypting that value
/// with FastBusiness.Crypto.dll's proprietary algorithm (see MainForm.DebugDecryptConnectStr's
/// doc comment). Bcode intentionally does not build a client for that: it would mean
/// reimplementing a vendor's proprietary crypto to call their private backend, and that
/// backend's project list spans FastBusiness's OTHER customers too (SHINHAN, VIRUCO,
/// APPOLLO, ...), not just Bee's own — a homebrew client for it is a very different, much
/// riskier thing than reading a file Bee's own already-licensed FCode install already keeps
/// in plain text on Bee's own machine. Reading THAT file is all this class does.
///
/// Config.xml has no field literally named "ID", but every &lt;project&gt; entry does carry
/// &lt;FolderName&gt; — verified against Bee's real, 59-entry Config.xml to match every ID
/// FCode's own "Projects" grid shows, always present and unique across all 59 entries. This
/// is what "ID" is derived from now. (An earlier version of this class tried to derive the ID
/// by parsing ProgramPath instead — e.g. "\\172.168.5.14\CustomerPro\FBI\BVNGOCPHU\SP2261\" ->
/// "BVNGOCPHU" — looking only for a "FBI"/"FBO" path segment. That missed real entries whose
/// ProgramPath uses a different product line, e.g. VIAGS/LONGSON/DSVINA under "FBFF",
/// HB_KMIX under "HRM", and DHYD/DHYD_TONGHOP/BVND2 under "G_WebUpload\FAP_PROJECT" or
/// "CustomerPro\FAP_PROJECT" — all of which DO have the right FolderName even though their
/// path doesn't fit the FBI/FBO shape. FolderName sidesteps guessing the path shape entirely,
/// so that parsing is now only a defensive fallback for the rare Config.xml entry that somehow
/// has no FolderName at all — every entry in Bee's real file has one.)
/// </summary>
public static class FCodeConfigImportService
{
    public record ImportedProject(
        string Id, string Server, string User, string SysDatabase, string AppDatabase,
        string LoginWLink, string ProgramPath, string SourcePath, string WorkingPath,
        string MobilePath, string RegistryName);

    public static List<ImportedProject> LoadAll(string configXmlPath)
    {
        var doc = XDocument.Load(configXmlPath);
        var result = new List<ImportedProject>();

        foreach (var proj in doc.Root?.Elements("project") ?? Enumerable.Empty<XElement>())
        {
            var programPath = (string?)proj.Element("ProgramPath") ?? "";
            var id = DeriveId(proj, programPath);
            if (id is null) continue; // no FolderName and doesn't follow the \FBI\{ID}\ style path either

            result.Add(new ImportedProject(
                Id: id,
                Server: (string?)proj.Element("servername") ?? "",
                User: (string?)proj.Element("User") ?? "",
                SysDatabase: (string?)proj.Element("df_Database") ?? "",
                AppDatabase: (string?)proj.Element("AppData") ?? "",
                LoginWLink: (string?)proj.Element("WLoginLink") ?? "",
                ProgramPath: programPath,
                SourcePath: (string?)proj.Element("SourcePath") ?? "",
                WorkingPath: (string?)proj.Element("WorkingPath") ?? "",
                MobilePath: (string?)proj.Element("mobile_path") ?? "",
                RegistryName: (string?)proj.Element("RegistryName") ?? ""));
        }

        return result;
    }

    /// <summary>First match by derived ID (case-insensitive) — see class doc for how ID is
    /// derived and why Config.xml itself has no field literally named "ID".</summary>
    public static ImportedProject? FindById(string configXmlPath, string id) =>
        LoadAll(configXmlPath).FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static string? DeriveId(XElement proj, string programPath)
    {
        // Primary source: <FolderName> — present and unique on every real project entry seen
        // so far, and it's literally what FCode's own "Projects" grid shows as the ID, whatever
        // product line (FBI/FBO/FBFF/HRM/FAP_PROJECT/...) the project happens to be under.
        var folderName = (string?)proj.Element("FolderName");
        if (!string.IsNullOrWhiteSpace(folderName)) return folderName.Trim();

        // Fallback, only for the rare entry with no FolderName at all: guess from ProgramPath
        // the same way the old FBI/FBO-only heuristic did, extended to the other product-line
        // markers observed in Bee's Config.xml.
        var parts = programPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var marker = parts[i];
            if ((marker == "FBI" || marker == "FBO" || marker == "FBFF" || marker == "HRM"
                    || marker == "FAP_PROJECT") && i + 1 < parts.Length)
                return parts[i + 1];
        }
        return null;
    }
}
