using Bcode.App.Controls;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>Hỏi giá trị tham số của store/function trước khi debug từng bước — mỗi tham số 1 ô, nhập đúng dạng T-SQL
/// ('abc', 5, NULL, '2026-01-31'...). Ô điền sẵn: giá trị truyền trong câu EXEC (nếu có), không thì DEFAULT của tham số, không thì NULL.</summary>
public sealed class StepParamsForm : Bcode.App.UI.ThemedForm
{
    private readonly List<(StepParam Param, TextBox Box)> _rows = new();

    public StepParamsForm(string routine, IReadOnlyList<StepParam> parameters, IReadOnlyDictionary<string, string> initial)
    {
        Text = "Debug từng bước — tham số";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        Width = 560;
        Height = Math.Min(160 + parameters.Count * 34, 640);
        MinimumSize = new Size(420, 200);

        var header = new Label
        {
            Text = $"{routine} — nhập giá trị tham số (dạng T-SQL: 'chuỗi', số, NULL):",
            Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 10, 8, 0),
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var p in parameters.Where(p => !p.ReadOnly))
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            grid.Controls.Add(new Label { Text = $"{p.Name}  ({p.Type})", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var value = initial.TryGetValue(p.Name, out var v) ? v : (p.Default.Length > 0 ? p.Default : "NULL");
            var box = new TextBox { Dock = DockStyle.Fill, Text = value, Margin = new Padding(0, 4, 0, 4) };
            grid.Controls.Add(box, 1, row);
            _rows.Add((p, box));
        }

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var cancel = PillButton.Flat("Huỷ");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var ok = PillButton.Flat("Bắt đầu debug", primary: true);
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        flow.Controls.Add(cancel);
        flow.Controls.Add(ok);
        bottom.Controls.Add(flow);

        Controls.Add(grid);
        Controls.Add(bottom);
        Controls.Add(header);
        Shown += (_, _) => { if (_rows.Count > 0) _rows[0].Box.Focus(); };
    }

    public Dictionary<string, string> Values =>
        _rows.ToDictionary(r => r.Param.Name, r => r.Box.Text.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Đọc tham số từ 1 câu gọi "EXEC proc @a = 1, 'x', @c = NULL": theo tên nếu có "@tên =", còn lại theo thứ tự vị trí.</summary>
    public static Dictionary<string, string> ParseCallArgs(string? callText, IReadOnlyList<StepParam> parameters)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(callText)) return result;

        var m = System.Text.RegularExpressions.Regex.Match(callText, @"\bEXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?[\w\.\[\]#$]+(?<args>.*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success) return result;
        var args = m.Groups["args"].Value.Trim().TrimEnd(';');
        if (args.Length == 0) return result;

        // Tách theo dấu phẩy ngoài chuỗi/ngoặc.
        var parts = new List<string>();
        var depth = 0; var inStr = false; var start = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c == '\'') inStr = !inStr;
            else if (!inStr && c == '(') depth++;
            else if (!inStr && c == ')') depth--;
            else if (!inStr && depth == 0 && c == ',') { parts.Add(args[start..i]); start = i + 1; }
        }
        parts.Add(args[start..]);

        var positional = 0;
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            var named = System.Text.RegularExpressions.Regex.Match(part, @"^(@[\w#$]+)\s*=\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (named.Success) { result[named.Groups[1].Value] = named.Groups[2].Value.Trim(); continue; }
            if (positional < parameters.Count) result[parameters[positional].Name] = part;
            positional++;
        }
        return result;
    }
}
