using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bcode.App.Services.ExcelToRpt;

/// <summary>Một mảnh của ô ghép '#?a + + !1.b': chữ, tham số (?nhãn) hoặc trường (!N.tên).</summary>
internal sealed class ConcatPart
{
    public string Type = "text";     // text | label | field
    public string? Text;
    public string? Name;
    public int Table;

    public JsonObject ToJson() => Type switch
    {
        "field" => new JsonObject { ["type"] = "field", ["table"] = Table, ["name"] = Name },
        "label" => new JsonObject { ["type"] = "label", ["name"] = Name },
        _ => new JsonObject { ["type"] = "text", ["text"] = Text },
    };
}

/// <summary>Kết quả phân tích nội dung một ô (dict "parsed" của bản Python).</summary>
internal sealed class ParsedCell
{
    public string Raw = "";
    public string Kind = "text";      // text | label | field | concat | skip
    public string? Name;
    public int Table;
    public string? Format;
    public string? Condition;
    public string? Text;
    public string? Reason;
    public (string attr, string cond)? StyleCondition;
    public List<ConcatPart>? Parts;
    /// <summary>Giá trị đã tính của ô công thức (chỉ dùng nội bộ, không ghi ra JSON).</summary>
    public object? Cached;
    /// <summary>true = dựng từ nội dung ô (khoá "raw" đứng đầu như parse_cell_value).</summary>
    public bool FromCell;

    public static ParsedCell Constructed(string kind) => new() { Kind = kind };

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (FromCell)
        {
            o["raw"] = Raw;
            if (StyleCondition is { } sc) o["style_condition"] = new JsonObject { ["attr"] = sc.attr, ["cond"] = sc.cond };
        }
        o["kind"] = Kind;
        switch (Kind)
        {
            case "field":
                o["table"] = Table;
                o["name"] = Name;
                if (Format is not null) o["format"] = Format;
                if (Condition is not null) o["condition"] = Condition;
                break;
            case "label":
                o["name"] = Name;
                break;
            case "concat":
                o["parts"] = new JsonArray((Parts ?? new()).Select(p => (JsonNode)p.ToJson()).ToArray());
                break;
            case "skip":
                o["reason"] = Reason;
                break;
            default:
                o["text"] = Text;
                break;
        }
        if (!FromCell) o["raw"] = Raw;
        return o;
    }
}

internal sealed class ItemFont
{
    public string Name = "Times New Roman";
    public double Size;
    public bool Bold, Italic;

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["size"] = JsonValue.Create(Size),
        ["bold"] = Bold,
        ["italic"] = Italic,
    };
}

/// <summary>Một object của report (một ô Excel, hoặc một phần của ô ghép sau khi tách).</summary>
internal sealed class LayoutItem
{
    public static readonly string[] Sides = { "top", "bottom", "left", "right" };

    public int Row, Col, X, YInRow, Width, Height;
    public int? Y;
    public ParsedCell Parsed = new();
    public ItemFont Font = new();
    public string Align = "left";
    public string VAlign = "center";
    public bool Wrap;
    /// <summary>Thứ tự khoá top/bottom/left/right; rỗng = object ẩn không có khung.</summary>
    public Dictionary<string, bool> Border = new();
    public Dictionary<string, string>? BorderStyles;
    public string? Bg, BorderColor, Color, NumberFormat;
    public bool Underline, Suppress;
    public bool OmitYInRow;

    public bool HasAnyBorder => Border.Values.Any(v => v);
    public bool BorderOn(string side) => Border.TryGetValue(side, out var v) && v;

    public LayoutItem Clone() => (LayoutItem)MemberwiseClone();

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["row"] = Row,
            ["col"] = Col,
            ["x"] = X,
        };
        if (!OmitYInRow) o["y_in_row"] = YInRow;
        o["width"] = Width;
        o["height"] = Height;
        o["parsed"] = Parsed.ToJson();
        o["font"] = Font.ToJson();
        o["align"] = Align;
        o["valign"] = VAlign;
        o["wrap"] = Wrap;
        var b = new JsonObject();
        foreach (var (k, v) in Border) b[k] = v;
        o["border"] = b;
        if (Bg is not null) o["bg"] = Bg;
        if (BorderColor is not null) o["border_color"] = BorderColor;
        if (Color is not null) o["color"] = Color;
        if (Underline) o["underline"] = true;
        if (NumberFormat is not null) o["number_format"] = NumberFormat;
        if (Suppress) o["suppress"] = true;
        if (BorderStyles is { Count: > 0 })
        {
            var s = new JsonObject();
            foreach (var (k, v) in BorderStyles) s[k] = v;
            o["border_styles"] = s;
        }
        if (Y is { } y) o["y"] = y;
        return o;
    }
}

internal static class LayoutJson
{
    /// <summary>Giống json.dumps(ensure_ascii=False, indent=2): tiếng Việt không bị escape \uXXXX.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
