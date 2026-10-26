using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Đoán ngôn ngữ của một đoạn chữ (dán từ ngoài vào, không có tên file) để tô màu — dùng cho Compare Text.
/// Khác <c>detectLanguage</c> của editor.js (theo ĐUÔI file): ở đây xét NỘI DUNG — mở bằng "&lt;" = XML (FCode: bộ tô SQL/JS trong CDATA),
/// "{" / "[" đọc được thành JSON = JSON, còn lại chấm điểm theo dấu hiệu T-SQL / C# / JavaScript; không rõ thì theo đuôi file (nếu có), cuối cùng là Text.
/// </summary>
public static class TextLanguageDetector
{
    /// <summary>Mã ngôn ngữ (cũng là giá trị của ô "Tô màu" trên thanh Compare Text).</summary>
    public const string Sql = "SQL", Xml = "XML", CSharp = "C#", JavaScript = "JavaScript", Json = "JSON", Text = "Text";

    public static readonly string[] All = { Sql, Xml, CSharp, JavaScript, Json, Text };

    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant;

    private static readonly Regex SqlSigns = new(
        @"^\s*(select|insert\s+into|update\s+\S+\s+set|delete\s+from|create\s+(or\s+alter\s+)?(proc|procedure|table|view|function|index|trigger)|alter\s+(proc|procedure|table|view|function)|declare\s+@|exec(ute)?\s|if\s+(not\s+)?exists|set\s+nocount|begin\s+tran|go\s*$|use\s+\[?\w)" +
        @"|\b(from\s+[\w\[\]#@.]+|inner\s+join|left\s+join|group\s+by|order\s+by|nvarchar|varchar\s*\(|isnull\s*\(|getdate\s*\(\)|with\s*\(\s*nolock\s*\))" +
        @"|@@?\w+|--", Opt);

    private static readonly Regex CSharpSigns = new(
        @"^\s*(using\s+[\w.]+\s*;|namespace\s+[\w.]+|\[[A-Z]\w+(\(.*\))?\]\s*$)" +
        @"|\b(public|private|protected|internal)\s+(static\s+|readonly\s+|async\s+|override\s+|virtual\s+|sealed\s+|partial\s+)*(class|record|struct|interface|enum|void|string|int|bool|Task|[A-Z]\w*(<[^>]*>)?)\s+\w+" +
        @"|\{\s*get\s*;|\bvar\s+\w+\s*=\s*new\b|\bstring\.\w+\(|\bnameof\(|\$""", Opt);

    private static readonly Regex JsSigns = new(
        @"\bfunction\s*\w*\s*\(|\b(const|let)\s+\w+\s*=|\bvar\s+\w+\s*=(?!\s*new\s+[A-Z]\w*\s*[<(])|===|!==|\bdocument\.|\bwindow\.|\bconsole\.\w+\(|\$\(|\brequire\(|\bexport\s+(default\s+)?(function|const|class)|\bimport\s+.+\s+from\s+['""]", Opt);

    /// <summary>Đoán ngôn ngữ của <paramref name="text"/>; <paramref name="path"/> (file đã chọn, nếu có) chỉ dùng khi nội dung không đủ dấu hiệu.</summary>
    public static string Detect(string? text, string? path = null)
    {
        var s = (text ?? "").TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (s.Length == 0) return ByExtension(path) ?? Text;
        if (s[0] == '<') return Xml;
        if (s[0] is '{' or '[' && s.Length < 5_000_000 && IsJson(s)) return Json;

        var sample = s.Length > 20_000 ? s[..20_000] : s;
        var sql = SqlSigns.Matches(sample).Count;
        var cs = CSharpSigns.Matches(sample).Count * 3;
        var js = JsSigns.Matches(sample).Count * 2;
        if (sql == 0 && cs == 0 && js == 0) return ByExtension(path) ?? Text;
        if (sql >= cs && sql >= js) return Sql;
        return cs >= js ? CSharp : JavaScript;
    }

    /// <summary>Ngôn ngữ theo đuôi file (cùng nhóm như detectLanguage của editor.js); null = đuôi lạ / không có file.</summary>
    public static string? ByExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Path.GetExtension(path.Trim().Trim('"')).ToLowerInvariant() switch
        {
            ".sql" => Sql,
            ".xml" or ".f" or ".ent" or ".config" or ".xsd" or ".xsl" or ".xslt" or ".resx" or ".csproj" or ".props" or ".targets" or ".svg" or ".aspx" or ".ascx" or ".html" or ".htm" => Xml,
            ".cs" => CSharp,
            ".js" => JavaScript,
            ".json" => Json,
            _ => null,
        };
    }

    private static bool IsJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); return true; }
        catch (JsonException) { return false; }
    }
}
