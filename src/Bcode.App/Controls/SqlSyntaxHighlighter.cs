using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Lightweight regex-based SQL syntax highlighter for a RichTextBox — colors
/// keywords, strings, comments, numbers, and highlights "GO" batch separators
/// the way FCode's own script viewer does, so a generated/opened script reads
/// as actual formatted SQL instead of one flat block of plain text.
///
/// Builds one RTF document and assigns it to <see cref="RichTextBox.Rtf"/> in a
/// single call, rather than the more obvious approach of looping matches and
/// calling Select()+SelectionColor for each one — that was the original
/// implementation, and it made scrolling a long, keyword-dense file (a
/// generated Grid/Dir XML mixing embedded SQL and JS, say — SQL keywords like
/// AS/IN/IS/SET/IF/END are common JS tokens too) visibly choppy, because every
/// one of those thousands of matches was its own round trip into the RichEdit
/// control. Handing it one finished RTF string instead lets RichEdit's own
/// native parser apply all the formatting in one pass, which is what makes a
/// long file in FCode's own script viewer stay smooth.
///
/// Deliberately simple otherwise: no external editor control/library, just
/// regex passes over the current text re-applied on load and (debounced)
/// while editing — not a real incremental lexer, but plenty for viewing/
/// lightly editing a CREATE TABLE/proc/trigger script.
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

    // Most of a FastBusiness Dir/Grid file's content is XML markup, not SQL — without these,
    // a file like that reads as almost entirely plain/black text (only whatever SQL keywords
    // happen to be embedded inside attribute values light up), which is what prompted adding
    // these three: tag names, attribute names, and attribute values (double-quoted, unlike
    // SQL's single-quoted strings above).
    private static readonly Regex XmlTagNameRegex = new(@"</?([A-Za-z][\w:.\-]*)", RegexOptions.Compiled);
    private static readonly Regex XmlAttrNameRegex = new(@"([A-Za-z_:][\w:.\-]*)(?=\s*=\s*"")", RegexOptions.Compiled);
    // No \r\n allowed inside — an attribute value is always one line; this deliberately
    // does NOT match the DOCTYPE's multi-line <!ENTITY Name "...multi-line SQL..."> values,
    // which just fall back to whatever their embedded SQL keywords/numbers/strings paint.
    private static readonly Regex XmlAttrValueRegex = new(@"""[^""\r\n]*""", RegexOptions.Compiled);

    private static readonly Color KeywordColor = Color.FromArgb(86, 156, 214);
    private static readonly Color NumberColor = Color.FromArgb(181, 206, 168);
    private static readonly Color StringColor = Color.FromArgb(214, 157, 133);
    private static readonly Color CommentColor = Color.FromArgb(96, 139, 78);
    private static readonly Color GoForeColor = Color.White;
    private static readonly Color GoBackColor = Color.FromArgb(150, 30, 30);
    private static readonly Color XmlTagColor = Color.FromArgb(78, 201, 176);
    private static readonly Color XmlAttrNameColor = Color.FromArgb(156, 220, 254);

    // Category ids painted into a per-character byte array. XML attribute values paint
    // first (lowest priority) so any SQL keyword/number/string embedded inside one (very
    // common — e.g. key="status = '1'") still shows through in its own color on top,
    // matching what the old per-match Select() calls already did for keywords found inside
    // a quoted value; everything else keeps that same later-wins override order.
    private const byte CatBase = 0, CatXmlAttrValue = 1, CatKeyword = 2, CatNumber = 3, CatString = 4, CatComment = 5, CatGo = 6, CatXmlTag = 7, CatXmlAttrName = 8;

    // Hard backstop against a truly pathological file — the RTF build below is a single
    // linear pass, not per-match interop calls, so this is generous; it exists only so an
    // enormous file doesn't spend real time/memory on a coloring pass nobody will read
    // through anyway.
    private const int MaxHighlightLength = 2_000_000;

    /// <summary>The regex passes shared by BuildHighlightedRtf and ApplyChunkedAsync below —
    /// same per-character category array either way, with no RichTextBox involved at all, so
    /// it's safe to call from a background thread. Just the categorization; what happens with
    /// it (one whole-document RTF build, or many small chunked runs) is up to the caller.</summary>
    private static byte[] ComputeCategories(string text)
    {
        var n = text.Length;
        var category = new byte[n];

        void Paint(IEnumerable<(int Index, int Length)> spans, byte cat)
        {
            foreach (var (idx, len) in spans)
            {
                var end = Math.Min(idx + len, n);
                for (var i = idx; i < end; i++) category[i] = cat;
            }
        }

        static IEnumerable<(int, int)> Spans(MatchCollection matches) => matches.Select(m => (m.Index, m.Length));

        Paint(Spans(XmlAttrValueRegex.Matches(text)), CatXmlAttrValue);
        Paint(Spans(KeywordRegex.Matches(text)), CatKeyword);
        Paint(Spans(NumberRegex.Matches(text)), CatNumber);
        Paint(Spans(StringRegex.Matches(text)), CatString);
        Paint(Spans(LineCommentRegex.Matches(text)), CatComment);
        Paint(Spans(BlockCommentRegex.Matches(text)), CatComment);
        Paint(Spans(GoRegex.Matches(text)), CatGo);
        // Only the captured tag-name group, not the whole match (which includes the
        // leading '<' or '</') — those should stay base-colored, like a real XML editor.
        Paint(XmlTagNameRegex.Matches(text).Select(m => (m.Groups[1].Index, m.Groups[1].Length)), CatXmlTag);
        Paint(Spans(XmlAttrNameRegex.Matches(text)), CatXmlAttrName);

        return category;
    }

    /// <summary>Builds one whole-document RTF string (used by Apply() below, for content that's
    /// small/fast enough that a single box.Rtf assignment won't be felt). For a script large
    /// enough to matter (Add Script on an 18k+-row table), use ApplyChunkedAsync instead — see
    /// its remarks for why this whole-string approach isn't safe to use there.</summary>
    public static string BuildHighlightedRtf(string text, Font font, Color baseColor) =>
        BuildRtf(text, ComputeCategories(text), font, baseColor);

    /// <summary>The actual fix for Add Script's "Not Responding" on a big script (18k+ rows) —
    /// "Nếu sinh ra view luôn thì bạn đang bị not responding so với fcode". Moving script
    /// generation and RTF-building off the UI thread (BuildHighlightedRtf above) wasn't enough:
    /// the popup still froze, because a single box.Rtf = wholeDocumentRtf assignment is itself
    /// one synchronous native RichEdit call that has to fully parse a many-MB RTF blob before
    /// returning — and THAT call still ran on the UI thread (it has to; RichTextBox isn't
    /// thread-safe), blocking it for however many seconds that parse takes. "Not Responding" is
    /// just Windows noticing the message loop hasn't been pumped in ~5s — it doesn't matter that
    /// the expensive work was "in the background" if the final handoff to the control is one
    /// long blocking call.
    ///
    /// This instead: (1) computes categories off the UI thread (same regex passes as above,
    /// cheap), (2) sets the box's plain text once — a plain assignment, not RTF, so there's no
    /// control-word parsing and the window is immediately scrollable/readable, then (3) walks
    /// the precomputed category runs applying SelectionColor/SelectionBackColor per run — the
    /// same Select()+SelectionColor mechanism the old (pre-RTF-batching) implementation used —
    /// but yields to the message loop on a wall-clock budget (a Stopwatch, not a fixed run
    /// count) so the window keeps responding to paint/input messages the whole time
    /// highlighting is being applied, regardless of how expensive each individual
    /// Select()+SelectionColor call turns out to be on a given machine/document — a fixed
    /// "yield every N runs" budget was tried first and still froze at just 1000 rows.
    ///
    /// It turned out that wasn't about run count at all: this box uses WordWrap=false (for
    /// horizontal scrolling through long generated SQL lines), and RichEdit recomputes the
    /// widest-line pixel width for the horizontal scrollbar on EVERY formatting change while
    /// WordWrap is off — not once, on every single Select()/SelectionColor call, seemingly
    /// regardless of WM_SETREDRAW. For a many-line script that recalculation, not the coloring
    /// itself, is what was actually freezing the window even at a few hundred rows — no amount
    /// of yielding between calls helps when a single call can itself run long. The fix: switch
    /// to WordWrap=true for a chunked-highlighted script — no horizontal extent to track at
    /// all with wrap on, so every Select()/SelectionColor call stays cheap regardless of line
    /// count. Deliberately NOT switching back to WordWrap=false afterwards — that single
    /// "restore" call would still force the same expensive one-time recalculation for the
    /// whole document, and for an 18k-row script there's no guarantee that one call alone
    /// stays under the freeze threshold either; wrapped long lines is a small readability
    /// trade for never risking that again. The time-based yield below stays as a second line
    /// of defense regardless of what's actually driving the per-call cost.</summary>
    public static async Task ApplyChunkedAsync(RichTextBox box, string text, Font font, Color baseColor)
    {
        if (box.IsDisposed || box.Disposing) return;

        var category = await Task.Run(() => ComputeCategories(text));
        if (box.IsDisposed || box.Disposing || !box.IsHandleCreated) return;

        box.WordWrap = true;

        SuspendPaint(box);
        try { box.Text = text; }
        finally { ResumePaint(box); }
        box.Select(0, 0);

        // Yield whenever a burst has run for more than ~25ms of wall-clock time — comfortably
        // under anything that could make Windows suspect the window is hung, and cheap to
        // check (one Stopwatch read per run). await Task.Yield() (not Task.Delay) posts the
        // continuation straight to the message loop via the WinForms SynchronizationContext and
        // resumes on the next message-loop iteration — sub-millisecond overhead per yield, so
        // yielding often doesn't itself make the total run take dramatically longer.
        const long MaxBurstMs = 25;
        var burst = System.Diagnostics.Stopwatch.StartNew();
        var n = text.Length;
        var i = 0;
        SuspendPaint(box);
        try
        {
            while (i < n)
            {
                if (box.IsDisposed || box.Disposing) return;

                var cat = category[i];
                var runStart = i;
                while (i < n && category[i] == cat) i++;

                if (cat != CatBase)
                {
                    box.Select(runStart, i - runStart);
                    ApplyRunFormatting(box, cat);
                }

                if (burst.ElapsedMilliseconds >= MaxBurstMs)
                {
                    ResumePaint(box);
                    await Task.Yield();
                    if (box.IsDisposed || box.Disposing) return;
                    SuspendPaint(box);
                    burst.Restart();
                }
            }
        }
        finally
        {
            ResumePaint(box);
            box.Select(0, 0);
        }
    }

    private static void ApplyRunFormatting(RichTextBox box, byte category)
    {
        switch (category)
        {
            case CatKeyword: box.SelectionColor = KeywordColor; break;
            case CatNumber: box.SelectionColor = NumberColor; break;
            case CatString: box.SelectionColor = StringColor; break;
            case CatXmlAttrValue: box.SelectionColor = StringColor; break;
            case CatComment: box.SelectionColor = CommentColor; break;
            case CatXmlTag: box.SelectionColor = XmlTagColor; break;
            case CatXmlAttrName: box.SelectionColor = XmlAttrNameColor; break;
            case CatGo:
                box.SelectionColor = GoForeColor;
                box.SelectionBackColor = GoBackColor;
                box.SelectionFont = new Font(box.Font, FontStyle.Bold);
                break;
        }
    }

    /// <summary>Async counterpart to Apply() below — same net effect (one box.Rtf assignment),
    /// but the regex passes + RTF string building run on a background thread first instead of
    /// blocking the UI thread in front of that assignment. Apply() alone is fine for a one-off
    /// explicit action (Open, Comment/Uncomment, ...) where a short synchronous pause is
    /// expected anyway, but calling it synchronously from the debounce Timer after pasting a
    /// few hundred lines ("dán 500 dòng vào ... đang hơi lag so với dán vào fcode") added that
    /// prep work on top of the (unavoidably blocking, RichTextBox isn't thread-safe) native
    /// Rtf parse — this removes everything in front of that final call that doesn't need to be
    /// there. Doesn't help an Add-Script-sized document (that's what ApplyChunkedAsync above is
    /// for) — this is for the much smaller, interactive-editing scale.</summary>
    public static async Task ApplyAsync(RichTextBox box)
    {
        if (box.IsDisposed || box.Disposing) return;
        if (box.TextLength == 0 || !box.IsHandleCreated) return;
        if (box.TextLength > MaxHighlightLength) return;

        var text = box.Text;
        var font = box.Font;
        var rtf = await Task.Run(() => BuildHighlightedRtf(text, font, AppColors.Text));

        if (box.IsDisposed || box.Disposing || !box.IsHandleCreated) return;
        // The user may have kept typing while this ran in the background — applying this now-
        // stale RTF over newer text would visibly revert it. Skip; TextChanged already
        // restarted the debounce timer for the newer text, so a fresh pass is already queued.
        if (box.Text != text) return;

        var selStart = box.SelectionStart;
        var selLen = box.SelectionLength;
        SuspendPaint(box);
        try
        {
            box.Rtf = rtf;
        }
        finally
        {
            var clampedStart = Math.Min(selStart, box.TextLength);
            var clampedLen = Math.Min(selLen, box.TextLength - clampedStart);
            box.Select(clampedStart, Math.Max(0, clampedLen));
            ResumePaint(box);
        }
    }

    public static void Apply(RichTextBox box)
    {
        // Guards against "Cannot access a disposed object" — the debounce Timer that calls
        // this can still have a pending Tick queued for a split second after the tab/control
        // that owns the RichTextBox was closed/disposed (e.g. View Script / closing a tab
        // right after typing). IsDisposed is safe to read even on a disposed control.
        if (box.IsDisposed || box.Disposing) return;
        if (box.TextLength == 0 || !box.IsHandleCreated) return;
        if (box.TextLength > MaxHighlightLength) return;

        var text = box.Text;

        // Read the theme's colors directly (not box.ForeColor) — this can run before
        // ThemeManager.Apply() has themed this control yet (LoadContent happens right
        // after `new ScriptEditorControl()`, before it's added to the tab and themed).
        var rtf = BuildHighlightedRtf(text, box.Font, AppColors.Text);

        var selStart = box.SelectionStart;
        var selLen = box.SelectionLength;
        SuspendPaint(box);
        try
        {
            box.Rtf = rtf;
        }
        finally
        {
            // Rtf reassignment resets the caret; clamp the old selection back into range
            // rather than dropping it, so re-highlighting mid-edit doesn't jump the caret.
            var clampedStart = Math.Min(selStart, box.TextLength);
            var clampedLen = Math.Min(selLen, box.TextLength - clampedStart);
            box.Select(clampedStart, Math.Max(0, clampedLen));
            ResumePaint(box);
        }
    }

    // Color table indices (1-based — index 0 in an RTF \colortbl is reserved for "auto").
    private const int CfBase = 1, CfKeyword = 2, CfNumber = 3, CfString = 4, CfComment = 5, CfGoFore = 6, CfGoBack = 7,
                       CfXmlTag = 8, CfXmlAttrName = 9;

    private static string BuildRtf(string text, byte[] category, Font font, Color baseColor)
    {
        var sb = new StringBuilder(text.Length + text.Length / 4 + 256);
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0\deflang1033");
        sb.Append(@"{\fonttbl{\f0\fnil\fcharset0 ").Append(EscapeRtfPlainText(font.Name)).Append(@";}}");
        sb.Append(@"{\colortbl ;");
        AppendColorEntry(sb, baseColor);
        AppendColorEntry(sb, KeywordColor);
        AppendColorEntry(sb, NumberColor);
        AppendColorEntry(sb, StringColor);
        AppendColorEntry(sb, CommentColor);
        AppendColorEntry(sb, GoForeColor);
        AppendColorEntry(sb, GoBackColor);
        AppendColorEntry(sb, XmlTagColor);
        AppendColorEntry(sb, XmlAttrNameColor);
        sb.Append('}');
        sb.Append(@"\viewkind4\uc1\pard\f0\fs").Append((int)Math.Round(font.SizeInPoints * 2)).Append(' ');

        var n = text.Length;
        var i = 0;
        while (i < n)
        {
            var cat = category[i];
            var runStart = i;
            while (i < n && category[i] == cat) i++;
            AppendRun(sb, text, runStart, i - runStart, cat);
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendColorEntry(StringBuilder sb, Color c) =>
        sb.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');

    private static void AppendRun(StringBuilder sb, string text, int start, int length, byte category)
    {
        switch (category)
        {
            case CatKeyword: sb.Append(@"\cf").Append(CfKeyword).Append(' '); break;
            case CatNumber: sb.Append(@"\cf").Append(CfNumber).Append(' '); break;
            case CatString: sb.Append(@"\cf").Append(CfString).Append(' '); break;
            case CatComment: sb.Append(@"\cf").Append(CfComment).Append(' '); break;
            case CatXmlAttrValue: sb.Append(@"\cf").Append(CfString).Append(' '); break;
            case CatXmlTag: sb.Append(@"\cf").Append(CfXmlTag).Append(' '); break;
            case CatXmlAttrName: sb.Append(@"\cf").Append(CfXmlAttrName).Append(' '); break;
            case CatGo:
                sb.Append(@"\cf").Append(CfGoFore).Append(@"\highlight").Append(CfGoBack).Append(@"\b ");
                break;
            default: sb.Append(@"\cf").Append(CfBase).Append(' '); break;
        }

        AppendRtfEscapedText(sb, text, start, length);

        if (category == CatGo) sb.Append(@"\b0\highlight0 ");
    }

    /// <summary>Appends <paramref name="text"/>[start..start+length) RTF-escaped: backslash/
    /// brace escaping, CRLF/CR/LF as \par, tab as \tab, and anything outside 7-bit ASCII
    /// (Vietnamese diacritics throughout this content) as a \uN? Unicode escape — required
    /// since RTF's plain text stream is otherwise limited to the font's 8-bit charset.</summary>
    private static void AppendRtfEscapedText(StringBuilder sb, string text, int start, int length)
    {
        var end = start + length;
        for (var i = start; i < end; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '\r':
                    continue; // paired \n (if present) below emits \par; a lone \r is harmless to drop
                case '\n':
                    sb.Append("\\par\r\n");
                    continue;
                case '\t':
                    sb.Append("\\tab ");
                    continue;
                case '\\':
                case '{':
                case '}':
                    sb.Append('\\').Append(ch);
                    continue;
            }

            if (ch < 128) sb.Append(ch);
            else sb.Append("\\u").Append((short)ch).Append('?');
        }
    }

    private static string EscapeRtfPlainText(string s)
    {
        var sb = new StringBuilder(s.Length);
        AppendRtfEscapedText(sb, s, 0, s.Length);
        return sb.ToString();
    }

    // WM_SETREDRAW: stops the RichTextBox repainting while the new Rtf is parsed in, so
    // this doesn't flicker.
    private const int WM_SETREDRAW = 0x000B;
    // EM_SETUNDOLIMIT (Rich Edit): caps how many actions the Undo queue keeps — 0 disables
    // recording new ones entirely, which is what stops highlighting from polluting Ctrl+Z.
    private const int EM_SETUNDOLIMIT = 0x0435;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, bool wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private static void SuspendPaint(RichTextBox box) => SendMessage(box.Handle, WM_SETREDRAW, false, 0);

    private static void ResumePaint(RichTextBox box)
    {
        SendMessage(box.Handle, WM_SETREDRAW, true, 0);
        box.Invalidate();
    }

    /// <summary>
    /// Permanently disables the RichTextBox's native Undo queue (EM_SETUNDOLIMIT 0), once,
    /// at setup. Apply() reassigns .Rtf on every highlight pass, and toggling the undo limit
    /// around just that reassignment — the seemingly obvious way to keep highlighting out of
    /// the Undo queue without disabling it outright — instead clears the queue completely
    /// (including the user's own real edits) each time it runs. Disabling it once here and
    /// letting UndoRedoTracker own Ctrl+Z/Ctrl+Y entirely avoids that.
    /// </summary>
    public static void DisableNativeUndo(RichTextBox box)
    {
        if (!box.IsHandleCreated)
        {
            box.HandleCreated += (_, _) => DisableNativeUndo(box);
            return;
        }
        SendMessage(box.Handle, EM_SETUNDOLIMIT, 0, 0);
    }
}
