using System.Text;
using System.Text.RegularExpressions;

namespace SqlWatch;

internal static class ProcCalls
{
    public static readonly string[] Events = ["SQL:BatchStarting", "RPC:Starting", "SP:Starting"];

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "sp_executesql", "sp_prepare", "sp_prepexec", "sp_execute", "sp_unprepare",
        "sp_cursor", "sp_cursoropen", "sp_cursorfetch", "sp_cursorclose",
        "sp_cursorprepare", "sp_cursorunprepare", "sp_cursoroption",
        "sp_reset_connection", "sp_getapplock", "sp_releaseapplock"
    };

    private static readonly Regex ExecPattern = new(
        @"(?<![\w@])exec(?:ute)?(?!\s+as\b)\s+(?:@\w+\s*=\s*)?(?<name>(?:\[[^\]]+\]|[\w$]+)(?:\s*\.\s*(?:\[[^\]]+\]|[\w$]+)){0,2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RpcPattern = new(
        @"^(?<name>(?:\[[^\]]+\]|[\w$]+)(?:\s*\.\s*(?:\[[^\]]+\]|[\w$]+)){0,2})\s*;\s*\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<(string Name, string Statement)> Find(string text)
    {
        var found = new List<(string Name, string Statement)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Scan(text, found, seen, 0);
        if (found.Count == 0)
        {
            var rpc = RpcPattern.Match(text.Trim());
            if (rpc.Success)
                Add(rpc.Groups["name"].Value, text.Trim(), found, seen);
        }
        return found;
    }

    public static string SelfCheck()
    {
        Expect("EXEC dbo.FastBusiness$X @a=1", "dbo.FastBusiness$X");
        Expect("exec @r = dbo.Foo", "dbo.Foo");
        Expect("exec sp_executesql N'exec dbo.Bar @p=1'", "dbo.Bar");
        Expect("sys.sp_who;1", "sys.sp_who");
        ExpectNone("SELECT 1");
        ExpectNone("execute as user = 'x'");
        ExpectNone("exec @sql");
        return "PASS";
    }

    private static void Scan(string text, List<(string Name, string Statement)> found, HashSet<string> seen, int depth)
    {
        if (depth > 3 || string.IsNullOrWhiteSpace(text))
            return;

        foreach (Match match in ExecPattern.Matches(text))
        {
            var name = Clean(match.Groups["name"].Value);
            var statement = Slice(text, match.Index);
            if (IsNoise(name))
            {
                foreach (var inner in FirstLiteral(text, match.Index))
                    Scan(inner, found, seen, depth + 1);
                continue;
            }
            Add(name, statement, found, seen);
        }
    }

    private static void Add(string name, string statement, List<(string Name, string Statement)> found, HashSet<string> seen)
    {
        if (name.Length == 0 || IsNoise(name) || !seen.Add(name))
            return;
        found.Add((name, statement.Length == 0 ? "EXEC " + name : statement));
    }

    private static bool IsNoise(string name)
    {
        var shortName = name;
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
            shortName = name[(dot + 1)..];
        return Noise.Contains(shortName);
    }

    private static string Clean(string name) => name.Replace("[", "").Replace("]", "").Replace(" ", "");

    private static string Slice(string text, int start)
    {
        var end = text.IndexOfAny(['\r', '\n'], start);
        if (end < 0)
            end = text.Length;
        end = Math.Min(end, start + 800);
        return text[start..end].Trim();
    }

    private static IEnumerable<string> FirstLiteral(string text, int start)
    {
        var quote = text.IndexOf('\'', start);
        if (quote < 0)
            yield break;

        var builder = new StringBuilder();
        for (var i = quote + 1; i < text.Length; i++)
        {
            if (text[i] != '\'')
            {
                builder.Append(text[i]);
                continue;
            }
            if (i + 1 < text.Length && text[i + 1] == '\'')
            {
                builder.Append('\'');
                i++;
                continue;
            }
            yield return builder.ToString();
            yield break;
        }
    }

    private static void Expect(string text, string name)
    {
        var found = Find(text);
        if (!found.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Không thấy " + name + " trong: " + text);
    }

    private static void ExpectNone(string text)
    {
        if (Find(text).Count > 0)
            throw new InvalidOperationException("Không được bắt procedure trong: " + text);
    }

    private static readonly Regex CreateHeader = new(
        @"\b(?:CREATE|ALTER)\s+PROC(?:EDURE)?\s+(?:\[[^\]]+\]|[\w$#@]+)(?:\s*\.\s*(?:\[[^\]]+\]|[\w$#@]+))?\s*\(?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Parameter = new(
        @"\G\s*@(?<name>[\w$#@]+)\s+(?:AS\s+)?(?<type>(?:\[[^\]]+\]|[\w$]+)(?:\s*\.\s*(?:\[[^\]]+\]|[\w$]+))?(?:\s*\(\s*[\w\s,]*\))?)(?:\s*=\s*(?<default>N?'(?:[^']|'')*'|[-+\w.]+))?(?<flags>(?:\s+(?:OUTPUT|OUT|READONLY|VARYING)\b)*)\s*(?<comma>,)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Comments = new(@"/\*.*?\*/|--[^\r\n]*", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Khung lời gọi procedure từ phần đầu định nghĩa: declare từng tham số (= mặc định hoặc NULL) rồi exec — dùng khi chỉ bắt được
    /// tên procedure (lời gọi RPC trên SQL 2008 không đọc được giá trị tham số qua DMV). Không đọc được tham số thì trả "EXEC tên".</summary>
    public static string CallSkeleton(string procName, string moduleText)
    {
        var parameters = new List<(string Name, string Type, string Default, bool Output, bool ReadOnly)>();
        var header = CreateHeader.Match(Comments.Replace(moduleText ?? "", " "));
        if (header.Success)
        {
            var text = Comments.Replace(moduleText ?? "", " ");
            var position = header.Index + header.Length;
            while (true)
            {
                var match = Parameter.Match(text, position);
                if (!match.Success || match.Length == 0)
                    break;
                var flags = match.Groups["flags"].Value;
                parameters.Add((match.Groups["name"].Value, Regex.Replace(match.Groups["type"].Value, @"\s+", " ").Trim(),
                    match.Groups["default"].Value, flags.Contains("OUT", StringComparison.OrdinalIgnoreCase),
                    flags.Contains("READONLY", StringComparison.OrdinalIgnoreCase)));
                position = match.Index + match.Length;
                if (!match.Groups["comma"].Success)
                    break;
            }
        }
        if (parameters.Count == 0)
            return "EXEC " + procName;

        var builder = new StringBuilder();
        builder.AppendLine("-- SqlWatch: lời gọi này (RPC) không đọc được giá trị tham số — điền giá trị rồi chạy.");
        foreach (var p in parameters)
            builder.AppendLine(p.ReadOnly
                ? $"declare @{p.Name} {p.Type}"
                : $"declare @{p.Name} {p.Type} = {(p.Default.Length > 0 ? p.Default : "NULL")}");
        builder.AppendLine();
        builder.Append("exec ").Append(procName).Append(' ')
            .Append(string.Join(", ", parameters.Select(p => "@" + p.Name + (p.Output ? " output" : ""))));
        return builder.ToString();
    }

    /// <summary>Câu có gọi procedure này không (so tên ngắn, bỏ schema / ngoặc vuông).</summary>
    public static bool Calls(string text, string procName)
    {
        var target = ShortName(Clean(procName));
        return target.Length > 0 && Find(text).Any(call => ShortName(call.Name).Equals(target, StringComparison.OrdinalIgnoreCase));
    }

    private static string ShortName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }
}
