using System.Xml.Linq;

namespace Bcode.App.Services;

public static class FCodeConfigImportService
{
    public class ImportedProject
    {
        public string Id { get; set; } = "";
        public string Server { get; set; } = "";
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
        public string SysDatabase { get; set; } = "";
        public string AppDatabase { get; set; } = "";
        public string LoginWLink { get; set; } = "";
        public string ProgramPath { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string MobilePath { get; set; } = "";
        public string WorkingPath { get; set; } = "";
        public string RegistryName { get; set; } = "";
    }

    public static ImportedProject? FindById(string xmlPath, string code)
    {
        if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath)) return null;

        var doc = XDocument.Load(xmlPath);

        // Duyệt qua tất cả các node project (hoặc bất kỳ node nào có dữ liệu dự án)
        foreach (var node in doc.Descendants())
        {
            // FCode dùng FolderName, User, ID hoặc id để định danh mã dự án
            var idVal = GetVal(node, "FolderName") 
                     ?? GetVal(node, "id") 
                     ?? GetVal(node, "ID") 
                     ?? GetVal(node, "User") 
                     ?? GetVal(node, "name");

            if (string.IsNullOrWhiteSpace(idVal)) continue;

            if (idVal.Trim().Equals(code.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var sysDb = GetVal(node, "df_Database") ?? GetVal(node, "SysDatabase") ?? "";
                var appDb = GetVal(node, "AppData") ?? GetVal(node, "AppDatabase") ?? "";

                // Nếu df_Database/AppData rỗng nhưng có dbaccess, tự tách lấy Sys và App
                var dbAccess = GetVal(node, "dbaccess");
                if (!string.IsNullOrWhiteSpace(dbAccess) && (string.IsNullOrWhiteSpace(sysDb) || string.IsNullOrWhiteSpace(appDb)))
                {
                    var parts = dbAccess.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        if (string.IsNullOrWhiteSpace(sysDb)) sysDb = parts[0];
                        if (string.IsNullOrWhiteSpace(appDb)) appDb = parts[1];
                    }
                }

                return new ImportedProject
                {
                    Id = idVal.Trim(),
                    Server = GetVal(node, "servername") ?? GetVal(node, "Server") ?? GetVal(node, "server") ?? "",
                    User = GetVal(node, "User") ?? GetVal(node, "user") ?? idVal.Trim(),
                    Password = GetVal(node, "Password") ?? "",
                    SysDatabase = sysDb.Trim(),
                    AppDatabase = appDb.Trim(),
                    LoginWLink = GetVal(node, "WLoginLink") ?? GetVal(node, "LoginWLink") ?? "",
                    ProgramPath = GetVal(node, "ProgramPath") ?? "",
                    SourcePath = GetVal(node, "SourcePath") ?? "",
                    MobilePath = GetVal(node, "mobile_path") ?? GetVal(node, "MobilePath") ?? "",
                    WorkingPath = GetVal(node, "WorkingPath") ?? "",
                    RegistryName = GetVal(node, "RegistryName") ?? @"Software\Fast"
                };
            }
        }

        return null;
    }

    private static string? GetVal(XElement element, string key)
    {
        var child = element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (child != null && !string.IsNullOrWhiteSpace(child.Value)) return child.Value;

        var attr = element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (attr != null && !string.IsNullOrWhiteSpace(attr.Value)) return attr.Value;

        return null;
    }
}