namespace Bcode.App.Controls;

/// <summary>Danh sách trang native (một dòng cho mỗi trang web đã có bản WinForms): <c>NativePages.Register("tên-trang.html", () =&gt; new XxxPage());</c></summary>
internal static class Pages
{
    public static void Register()
    {
        NativePages.Register("comparestructure.html", () => new CompareStructurePage());
        NativePages.Register("wcommandduplicate.html", () => new DuplicateMenuPage());
        NativePages.Register("uiscale.html", () => new UiScalePage());
        NativePages.Register("listeditor.html", () => new ListEditorPage());
        NativePages.Register("rowdetail.html", () => new RowDetailPage());
        NativePages.Register("columndesign.html", () => new ColumnDesignPage());
        NativePages.Register("stepparams.html", () => new StepParamsPage());
        NativePages.Register("copysourcestandard.html", () => new CopySourceStandardPage());
        NativePages.Register("licensekey.html", () => new LicenseKeyPage());
        NativePages.Register("editproject.html", () => new EditProjectPage());
        NativePages.Register("aihistory.html", () => new AiHistoryPage());
        NativePages.Register("scriptviewbar.html", () => new ScriptViewBarPage());
        NativePages.Register("grantsource.html", () => new GrantSourcePage());
        NativePages.Register("addsource.html", () => new AddSourcePage());
        NativePages.Register("copymulti.html", () => new CopyMultiPage());
        NativePages.Register("catalogclone.html", () => new CatalogClonePage());
        NativePages.Register("commandpalette.html", () => new CommandPalettePage());
        NativePages.Register("quickaccess.html", () => new QuickAccessPage());
        NativePages.Register("sqlhistory.html", () => new SqlHistoryPage());
        NativePages.Register("wcommandedit.html", () => new WCommandEditPage());
        NativePages.Register("appcommandedit.html", () => new AppCommandEditPage());
        NativePages.Register("connections.html", () => new ConnectionsPage());
        NativePages.Register("apideclaration.html", () => new ApiDeclarationPage());
        NativePages.Register("usages.html", () => new UsagesPage());
        NativePages.Register("filereference.html", () => new FileReferencePage());
        NativePages.Register("decryptsql.html", () => new DecryptSqlPage());
        NativePages.Register("includecheck.html", () => new IncludeCheckPage());
        NativePages.Register("genupdate.html", () => new GenUpdatePage());
        // bbxn.html (Biên bản xác nhận): user chọn giữ dạng WebView ở chế độ App (2026-10-15) — BbxnPage còn trong NativePages.Bbxn.cs, chỉ không đăng ký.
        // NativePages.Register("bbxn.html", () => new BbxnPage());
        NativePages.Register("cfsdiag.html", () => new CfsDiagPage());
        NativePages.Register("setupeinvoice.html", () => new SetupEInvoicePage());
        NativePages.Register("comparebar.html", () => new CompareBarPage());
        NativePages.Register("reportmodes.html", () => new ReportModesPage());
        NativePages.Register("sqlhints-help.html", () => new SqlHintsHelpPage());
        NativePages.Register("filelookupbar.html", () => new FileLookupBarPage());
        NativePages.Register("searchbox.html", () => new FileLookupSearchBoxPage());
        NativePages.Register("filelookuppreview.html", () => new FileLookupPreviewBarPage());
        NativePages.Register("note.html", () => new NotePage());
        NativePages.Register("sqlprofilerbar.html", () => new SqlProfilerBarPage());
        NativePages.Register("rptschema.html", () => new RptSchemaPage());
    }
}
