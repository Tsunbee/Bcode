using System.Text;
using System.Text.Json.Nodes;
using Bcode.App.Services.ExcelToRpt;
using Xunit;

namespace Bcode.ExcelToRpt.Tests;

/// <summary>
/// Luồng /api/* của RptApiBridge (thay server.py). Phần parse/xsd chạy ở mọi máy; phần sinh
/// .rpt thật cần RptGenerator.exe + Crystal runtime và thư mục template, khai qua biến môi
/// trường (không có thì bỏ qua):
///   RPT_TEST_GENERATOR    = ...\RptGenerator.exe
///   RPT_TEST_TEMPLATE_DIR = thư mục chứa template .rpt (vd D:\phongnt\Convert)
/// </summary>
public class BridgeTests
{
    private static string GoldenDir => Path.Combine(AppContext.BaseDirectory, "Golden");

    private static RptApiBridge NewBridge(string? templateDir, string? exe) =>
        new(new RptGeneratorRunner(() => exe ?? Path.Combine(Path.GetTempPath(), "khong-co", "RptGenerator.exe")),
            new TemplateLocator(() => templateDir is null ? Array.Empty<string>() : new[] { templateDir }, () => ""),
            () => "", _ => { });

    private static JsonObject Json(ApiResponse r) => (JsonObject)JsonNode.Parse(r.BodyText)!;

    [Fact]
    public async Task Parse_returns_layout_same_as_parser()
    {
        using var bridge = NewBridge(null, null);
        var xlsx = Path.Combine(GoldenDir, "CBTran__402ea3.xlsx");
        var r = await bridge.HandleAsync("POST", "/api/parse?name=" + Uri.EscapeDataString("CBTran.xlsx"), File.ReadAllBytes(xlsx));
        Assert.Equal(200, r.Status);
        var d = Json(r);
        Assert.Equal("CBTran", (string?)d["layout"]!["report_name"]);
        Assert.Null(d["template"]);
        Assert.Contains("OK:", (string?)d["log"]);
    }

    [Fact]
    public async Task Parse_rejects_path_traversal_in_name()
    {
        using var bridge = NewBridge(null, null);
        var xlsx = Path.Combine(GoldenDir, "CBTran__402ea3.xlsx");
        var r = await bridge.HandleAsync("POST", "/api/parse?name=" + Uri.EscapeDataString(@"..\..\evil.xlsx"), File.ReadAllBytes(xlsx));
        Assert.Equal(200, r.Status);
        Assert.True(File.Exists(Path.Combine(bridge.WorkDir, "evil.xlsx")));
    }

    [Fact]
    public async Task File_endpoint_only_serves_session_files()
    {
        using var bridge = NewBridge(null, null);
        var outside = Path.Combine(GoldenDir, "CBTran__402ea3.xlsx");
        var r = await bridge.HandleAsync("GET", "/api/file?p=" + Uri.EscapeDataString(outside), Array.Empty<byte>());
        Assert.Equal(404, r.Status);
    }

    [Fact]
    public async Task Generate_without_generator_reports_clearly()
    {
        using var bridge = NewBridge(null, null);
        await bridge.HandleAsync("POST", "/api/xsd?name=a.xsd", Encoding.UTF8.GetBytes(SampleXsd));
        var body = new JsonObject { ["layout"] = new JsonObject { ["report_name"] = "x" }, ["pdf"] = false };
        var r = await bridge.HandleAsync("POST", "/api/generate", Encoding.UTF8.GetBytes(body.ToJsonString()));
        Assert.Equal(400, r.Status);
        Assert.Contains("RptGenerator.exe", (string?)Json(r)["error"]);
    }

    [Fact]
    public async Task Generate_refuses_to_overwrite_existing_rpt()
    {
        using var bridge = NewBridge(null, null);
        var outDir = Path.Combine(bridge.WorkDir, "out");
        Directory.CreateDirectory(outDir);
        var existing = Path.Combine(outDir, "CBTran.rpt");
        File.WriteAllText(existing, "ban cu");

        var body = new JsonObject
        {
            ["layout"] = new JsonObject { ["report_name"] = "CBTran" }, ["pdf"] = false,
            ["out_dir"] = outDir, ["out_name"] = "cbtran",        // khác hoa/thường vẫn là trùng
        };
        var r = await bridge.HandleAsync("POST", "/api/generate", Encoding.UTF8.GetBytes(body.ToJsonString()));
        var d = Json(r);
        Assert.False((bool)d["ok"]!);
        Assert.Contains("không ghi đè", (string?)d["error"]);
        Assert.Equal("ban cu", File.ReadAllText(existing));
    }

    [Fact]
    public async Task Xsd_lists_tables_and_fields()
    {
        using var bridge = NewBridge(null, null);
        var r = await bridge.HandleAsync("POST", "/api/xsd?name=a.xsd", Encoding.UTF8.GetBytes(SampleXsd));
        Assert.Equal(200, r.Status);
        var t = Json(r)["tables"]!.AsArray();
        Assert.Single(t);
        Assert.Equal("Table1", (string?)t[0]!["name"]);
        Assert.Equal(new[] { "so_ct:string", "tien:number", "ngay_ct:datetime" },
            t[0]!["fields"]!.AsArray().Select(f => $"{f!["name"]}:{f["type"]}"));
    }

    [Fact]
    public async Task Report_xml_with_external_entities_is_readable()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE report [ <!ENTITY b SYSTEM ".\Include\BaseCurrency.xml"> ]>
            <report xmlns="urn:schemas-fast-com:data-report">
              <fields>
                <field name="h_so_ct" type="String"><header v="Số: " e="Number:"/></field>
                &b;
              </fields>
              <forms>
                <form id="01" reportFile="CPTran_01"><header v="Phiếu chi"/>
                  <fields><field name="title"><header v="PHIẾU CHI"/></field></fields>
                </form>
              </forms>
            </report>
            """;
        using var bridge = NewBridge(null, null);
        var r = await bridge.HandleAsync("POST", "/api/reportxml?name=r.xml&form=01", Encoding.UTF8.GetBytes(xml));
        Assert.Equal(200, r.Status);
        var d = Json(r);
        Assert.Equal("01", (string?)d["form"]);
        Assert.Equal(new[] { "h_so_ct=Số: ", "title=PHIẾU CHI" },
            d["parameters"]!.AsArray().Select(p => $"{p!["name"]}={p["v"]}"));
        Assert.Equal("Phiếu chi", (string?)d["forms"]![0]!["header"]);
    }

    [Fact]
    public async Task Generate_real_rpt_when_crystal_is_available()
    {
        var exe = Environment.GetEnvironmentVariable("RPT_TEST_GENERATOR");
        var tplDir = Environment.GetEnvironmentVariable("RPT_TEST_TEMPLATE_DIR");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe) || string.IsNullOrEmpty(tplDir)) return;

        using var bridge = NewBridge(tplDir, exe);
        var xlsx = Path.Combine(GoldenDir, "CBTran__402ea3.xlsx");
        var parsed = Json(await bridge.HandleAsync("POST", "/api/parse?name=CBTran.xlsx", File.ReadAllBytes(xlsx)));
        Assert.EndsWith("CBTran.rpt", (string?)parsed["template"]);

        var outDir = Path.Combine(bridge.WorkDir, "out");
        Directory.CreateDirectory(outDir);
        var req = new JsonObject
        {
            ["layout"] = parsed["layout"]!.DeepClone(), ["pdf"] = true,
            ["out_dir"] = outDir, ["out_name"] = "CBTran_test",
        };
        var r = Json(await bridge.HandleAsync("POST", "/api/generate", Encoding.UTF8.GetBytes(req.ToJsonString())));
        Assert.True((bool)r["ok"]!, (string?)r["log"]);
        Assert.True((bool)r["saved"]!, (string?)r["log"]);
        Assert.True(File.Exists(Path.Combine(outDir, "CBTran_test.rpt")));
        Assert.NotNull((string?)r["pdf"]);

        var fields = await bridge.HandleAsync("POST", "/api/fields", Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(200, fields.Status);
    }

    private const string SampleXsd = """
        <?xml version="1.0" encoding="utf-8"?>
        <xs:schema id="DS" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
          <xs:element name="DS" msdata:IsDataSet="true">
            <xs:complexType>
              <xs:choice minOccurs="0" maxOccurs="unbounded">
                <xs:element name="Table1">
                  <xs:complexType>
                    <xs:sequence>
                      <xs:element name="so_ct" type="xs:string" minOccurs="0" />
                      <xs:element name="tien" type="xs:decimal" minOccurs="0" />
                      <xs:element name="ngay_ct" type="xs:dateTime" minOccurs="0" />
                    </xs:sequence>
                  </xs:complexType>
                </xs:element>
              </xs:choice>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;
}
