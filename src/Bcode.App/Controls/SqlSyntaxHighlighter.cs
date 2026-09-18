using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Lightweight regex-based SQL syntax highlighter for a RichTextBox — colors
/// keywords, strings, comments, numbers, and highlights "GO" batch separators
/// the way FCode's own script viewer does, so a generated/opened script reads
/// as actual formatted SQL instead of one flat block of plain text.
///
/// Deliberately simple: no external editor control/library, just regex passes
/// over the current text re-applied on load and (debounced) while editing —
/// not a real incremental lexer, but plenty for viewing/lightly editing a
/// CREATE TABLE/proc/trigger script.
/// </summary>
public static class SqlSyntaxHighlighter
{
    private static readonly string[] Keywords =
    {
        "SELECT","FROM","WHERE","INSERT","INTO","UPDATE","DELETE","CREATE","ALTER","DROP",
        "TABLE","VIEW","PROCEDURE","FUNCTION","TRIGGER","INDEX","PRIMARY","KEY","FOREIGN",
        "REFERENCES","NOT","NULL","DEFAULT","UNIQUE","CLUSTERED","NONCLUSTERED","CONSTRAINT",
        "AS","JOIN","INNER","LEFT","RIGHT","OUTER","ON","GROUP","BY","ORDER","HAVING","UNION",
        "ALL","DISTINCT","AND","OR","IN","EXISTS","IS","LIKE","BETWEEN","CASE","WHEN","THEN",
        "ELSE","END","BEGIN","IF","RETURN","DECLARE","SET","EXEC","EXECUTE","VALUES","WITH",
        "ENCRYPTION","IDENTITY","CHAR","VARCHAR","NVARCHAR","NUMERIC","DECIMAL","INT","TINYINT",
        "SMALLINT","BIGINT","SMALLDATETIME","DATETIME","DATE","BIT","MAX","ASC","DESC",
        "OBJECT_ID","OBJECT_DEFINITION","TOP","CAST","CONVERT","ISNULL","COALESCE"
    };

    private static readonly Regex KeywordRegex = new(
        @"\b(" + string.Join("|", Keywords) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Exposed for the "Suggest Param/Caret" autocomplete in RawSqlControl — the
    /// same keyword list used for coloring doubles as the keyword half of suggestions.</summary>
    public static IReadOnlyList<string> KeywordList { get; } = Keywords;

    /// <summary>Backs the "Default Type" dropdown (UPPER Keyword / lower Keyword) — rewrites
    /// every recognized SQL keyword in <paramref name="sql"/> to the chosen case, leaving
    /// identifiers/strings/comments untouched (KeywordRegex already only matches keywords).</summary>
    public static string TransformKeywordCase(string sql, bool toUpper) =>
        KeywordRegex.Replace(sql, m => toUpper ? m.Value.ToUpperInvariant() : m.Value.ToLowerInvariant());

    private static readonly Regex GoRegex = new(@"^[ \t]*GO[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex StringRegex = new(@"'([^']|'')*'", RegexOptions.Compiled);
    private static readonly Regex LineCommentRegex = new(@"--[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex BlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"(?<![\w$])-?\d+(\.\d+)?(?!\w)", RegexOptions.Compiled);

    private static readonly Color KeywordColor = Color.FromArgb(86, 156, 214);
    private static readonly Color NumberColor = Color.FromArgb(181, 206, 168);
    private static readonly Color StringColor = Color.FromArgb(214, 157, 133);
    private static readonly Color CommentColor = Color.FromArgb(96, 139, 78);
    private static readonly Color GoForeColor = Color.White;
    private static readonly Color GoBackColor = Color.FromArgb(150, 30, 30);

    public static void Apply(RichTextBox box)
    {
        // Guards against "Cannot access a disposed object" — the debounce Timer that calls
        // this can still have a pending Tick queued for a split second after the tab/control
        // that owns the RichTextBox was closed/disposed (e.g. View Script / closing a tab
        // right after typing). IsDisposed is safe to read even on a disposed control.
        if (box.IsDisposed || box.Disposing) return;
        if (box.TextLength == 0 || !box.IsHandleCreated) return;

        var text = box.Text;
        var selStart = box.SelectionStart;
        var selLen = box.SelectionLength;
        // Read the theme's colors directly (not box.ForeColor/BackColor) — this can run
        // before ThemeManager.Apply() has themed this control yet (LoadContent happens
        // right after `new ScriptEditorControl()`, before it's added to the tab and
        // themed), and RTF per-character SelectionColor would otherwise "freeze in" the
        // wrong (pre-theme) color permanently instead of picking up the real one later.
        var baseColor = AppColors.Text;
        var backColor = AppColors.Input;

        SuspendPaint(box);
        try
        {
            box.SelectAll();
            box.SelectionColor = baseColor;
            box.SelectionBackColor = backColor;
            box.SelectionFont = box.Font;

            Mark(box, KeywordRegex, text, KeywordColor);
            Mark(box, NumberRegex, text, NumberColor);
            Mark(box, StringRegex, text, StringColor);
            Mark(box, LineCommentRegex, text, CommentColor);
            Mark(box, BlockCommentRegex, text, CommentColor);

            foreach (Match m in GoRegex.Matches(text))
            {
                box.Select(m.Index, m.Length);
                box.SelectionColor = GoForeColor;
                box.SelectionBackColor = GoBackColor;
                box.SelectionFont = new Font(box.Font, FontStyle.Bold);
            }
        }
        finally
        {
            box.Select(selStart, selLen);
            box.SelectionColor = baseColor;
            ResumePaint(box);
        }
    }

    private static void Mark(RichTextBox box, Regex regex, string text, Color color)
    {
        foreach (Match m in regex.Matches(text))
        {
            box.Select(m.Index, m.Length);
            box.SelectionColor = color;
        }
    }

    // WM_SETREDRAW: stops the RichTextBox repainting/scrolling on every intermediate
    // Select() call while we recolor, so this doesn't flicker or jump the caret.
    private const int WM_SETREDRAW = 0x000B;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, bool wParam, int lParam);

    private static void SuspendPaint(RichTextBox box) => SendMessage(box.Handle, WM_SETREDRAW, false, 0);

    private static void ResumePaint(RichTextBox box)
    {
        SendMessage(box.Handle, WM_SETREDRAW, true, 0);
        box.Invalidate();
    }
}
