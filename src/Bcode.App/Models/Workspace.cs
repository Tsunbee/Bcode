namespace Bcode.App.Models;

/// <summary>
/// A "WS" / Project entry — one row = one customer/site connection profile.
/// Field names mirror FastBusiness's own "Edit Project" dialog (Server Name,
/// Login User/Password, Sys Data, App Data, Program Path, Source Path, Mobile
/// Path, Working Path, Registry Name, Login WLink) so a real project's config
/// maps here 1:1, rather than a single simplified "Database" field.
/// </summary>
public class Workspace
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public bool IntegratedSecurity { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>System/shared database (menu, users, wcommand...) — "Sys Data" in FastBusiness.</summary>
    public string SysDatabase { get; set; } = "";

    /// <summary>Business/transaction database (m21$..., c21$..., d21$...) — "App Data".</summary>
    public string AppDatabase { get; set; } = "";

    /// <summary>Project/customer short code — "ID" in Edit Project.</summary>
    public string ProjectId { get; set; } = "";

    /// <summary>Web login URL — "Login WLink".</summary>
    public string LoginWLink { get; set; } = "";

    /// <summary>UNC path to the compiled program/executables — "Program Path".</summary>
    public string ProgramPath { get; set; } = "";

    /// <summary>
    /// UNC path to the web source root — "Source Path". Used by File Lookup to browse
    /// App_Data/Controllers/{Dir,Filter,Grid,Report,Lookup} and Templates.
    /// </summary>
    public string SourcePath { get; set; } = "";

    /// <summary>UNC path for the mobile app's own source/config — "Mobile Path".</summary>
    public string MobilePath { get; set; } = "";

    /// <summary>UNC path used for update/working files — "Working Path".</summary>
    public string WorkingPath { get; set; } = "";

    /// <summary>Registry key name equivalent, kept only for reference/notes — "Registry Name".</summary>
    public string RegistryName { get; set; } = "";

    public override string ToString() => Name;

    /// <summary>Builds a connection string to either the Sys Data or App Data database.</summary>
    public string BuildConnectionString(bool useSysDatabase = false)
    {
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = useSysDatabase ? SysDatabase : AppDatabase,
            TrustServerCertificate = true,
            ConnectTimeout = 8
        };

        if (IntegratedSecurity)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = User;
            b.Password = Password;
        }

        return b.ConnectionString;
    }
}
