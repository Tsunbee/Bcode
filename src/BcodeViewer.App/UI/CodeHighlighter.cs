using System.Text.RegularExpressions;

namespace BcodeViewer.App.UI;

/// <summary>
/// Lightweight syntax coloring for the Hint Code detail panel's code box, keyed off the
/// snippet's own Category (JS/SQL/XML/CSS) rather than auto-detection. Uses plain
/// RichTextBox.Select()+SelectionColor per match instead of building one RTF string per
/// pass (the approach Bcode.App's SqlSyntaxHighlighter uses for the main file preview) —
/// that rewrite existed specifically because RTF-per-keystroke lagged on full-size source
/// files, and because raw RTF needs \uNNNN escaping for anything outside ASCII, which this
/// content can't avoid (attribute values here are routinely Vietnamese, e.g. "Mã lô").
/// Hint snippets are short by nature (a library of reusable fragments, not whole files), so
/// the simpler per-match coloring is both correct for Unicode and in no way a perf problem.
/// Deliberately a standalone, smaller port — BcodeViewer has no project reference to
/// Bcode.App by design (see MainForm.cs's doc comment).
/// </summary>
public static class CodeHighlighter
{
    private static readonly Color BaseColor = Color.FromArgb(212, 212, 212);
    private static readonly Color KeywordColor = Color.FromArgb(197, 134, 192);
    private static readonly Color StringColor = Color.FromArgb(206, 145, 120);
    private static readonly Color CommentColor = Color.FromArgb(106, 153, 85);
    private static readonly Color NumberColor = Color.FromArgb(181, 206, 168);
    private static readonly Color TagColor = Color.FromArgb(86, 156, 214);
    private static readonly Color AttrNameColor = Color.FromArgb(156, 220, 254);
    private static readonly Color AttrValueColor = Color.FromArgb(206, 145, 120);

    private static readonly HashSet<string> JsKeywords = new(StringComparer.Ordinal)
    {
        "var", "let", "const", "function", "return", "if", "else", "for", "while", "do", "break",
        "continue", "new", "typeof", "instanceof", "this", "null", "true", "false", "undefined",
        "try", "catch", "finally", "throw", "switch", "case", "default", "in", "of", "class",
        "extends", "super", "void", "delete", "yield", "async", "await"
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "insert", "into", "values", "update", "set", "delete", "join",
        "inner", "left", "right", "outer", "on", "and", "or", "not", "null", "is", "as", "order",
        "by", "group", "having", "union", "all", "distinct", "top", "exists", "in", "like",
        "between", "case", "when", "then", "else", "end", "declare", "begin", "commit", "rollback",
        "transaction", "exec", "procedure", "create", "alter", "table", "drop", "cast", "print"
    };

    public static void Highlight(RichTextBox box, string category)
    {
        if (box.TextLength == 0) return;
        var text = box.Text;
        var selStart = box.SelectionStart;
        var selLen = box.SelectionLength;

        box.SelectAll();
        box.SelectionColor = BaseColor;

        switch ((category ?? "").ToUpperInvariant())
        {
            case "XML": HighlightXml(box, text); break;
            case "SQL": HighlightKeywordsAndLiterals(box, text, SqlKeywords, "--"); break;
            case "CSS": HighlightCss(box, text); break;
            default: HighlightKeywordsAndLiterals(box, text, JsKeywords, "//"); break; // JS
        }

        box.SelectionStart = selStart;
        box.SelectionLength = selLen;
        box.SelectionColor = BaseColor;
    }

    private static void ApplyColor(RichTextBox box, Match m, Color color)
    {
        box.Select(m.Index, m.Length);
        box.SelectionColor = color;
    }

    private static void HighlightXml(RichTextBox box, string text)
    {
        foreach (Match m in Regex.Matches(text, @"</?[A-Za-z][\w:.-]*")) ApplyColor(box, m, TagColor);
        foreach (Match m in Regex.Matches(text, @"([A-Za-z_:][\w:.-]*)(?=\s*=)")) ApplyColor(box, m, AttrNameColor);
        foreach (Match m in Regex.Matches(text, "\"[^\"]*\"")) ApplyColor(box, m, AttrValueColor);
        foreach (Match m in Regex.Matches(text, @"<!\[CDATA\[[\s\S]*?\]\]>")) ApplyColor(box, m, StringColor);
        foreach (Match m in Regex.Matches(text, "<!--[\\s\\S]*?-->")) ApplyColor(box, m, CommentColor);
    }

    private static void HighlightKeywordsAndLiterals(RichTextBox box, string text, HashSet<string> keywords, string lineComment)
    {
        foreach (Match m in Regex.Matches(text, "'([^'\\\\]|\\\\.)*'|\"([^\"\\\\]|\\\\.)*\"")) ApplyColor(box, m, StringColor);
        foreach (Match m in Regex.Matches(text, @"\b\d+(\.\d+)?\b")) ApplyColor(box, m, NumberColor);
        foreach (Match m in Regex.Matches(text, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
            if (keywords.Contains(m.Value)) ApplyColor(box, m, KeywordColor);
        foreach (Match m in Regex.Matches(text, Regex.Escape(lineComment) + ".*")) ApplyColor(box, m, CommentColor);
        foreach (Match m in Regex.Matches(text, @"/\*[\s\S]*?\*/")) ApplyColor(box, m, CommentColor);
    }

    private static void HighlightCss(RichTextBox box, string text)
    {
        foreach (Match m in Regex.Matches(text, "[A-Za-z-]+(?=\\s*:)")) ApplyColor(box, m, AttrNameColor);
        foreach (Match m in Regex.Matches(text, "'[^']*'|\"[^\"]*\"")) ApplyColor(box, m, StringColor);
        foreach (Match m in Regex.Matches(text, @"/\*[\s\S]*?\*/")) ApplyColor(box, m, CommentColor);
    }
}
