using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>
/// Line-based diff for the "Compare Text" tool. Implements a straightforward
/// LCS (longest common subsequence) diff — good enough for comparing script
/// files up to a few thousand lines; no external diff library needed.
/// </summary>
public class DiffService
{
    /// <summary>Số dòng sửa/chèn/xoá tối đa mà Myers xử lý (bộ nhớ ~ số này bình phương): hơn thế thì rơi về bảng LCS / so theo vị trí.</summary>
    private const int MaxMyersEdits = 2500;

    /// <summary>Số ô tối đa của bảng LCS (dòng giữa × dòng giữa) — quá thì so từng dòng theo vị trí thay vì tốn hàng trăm MB bộ nhớ.</summary>
    private const long MaxLcsCells = 25_000_000;

    public List<DiffLine> Diff(string leftText, string rightText)
    {
        var left = SplitLines(leftText);
        var right = SplitLines(rightText);

        // Bỏ phần đầu và phần cuối giống hệt nhau trước: file lớn mà chỉ sửa vài dòng thì phần cần so thực sự rất nhỏ (nhanh, ít bộ nhớ).
        var pre = 0;
        while (pre < left.Length && pre < right.Length && left[pre] == right[pre]) pre++;
        var suf = 0;
        while (suf < left.Length - pre && suf < right.Length - pre && left[left.Length - 1 - suf] == right[right.Length - 1 - suf]) suf++;

        var result = new List<DiffLine>(left.Length + 8);
        for (var k = 0; k < pre; k++)
            result.Add(new DiffLine { Kind = DiffKind.Equal, Text = left[k], LeftLineNo = k + 1, RightLineNo = k + 1 });

        var midL = left.Length - pre - suf;
        var midR = right.Length - pre - suf;
        DiffMiddle(left, right, pre, midL, midR, result);

        for (var k = 0; k < suf; k++)
        {
            var li = left.Length - suf + k;
            var ri = right.Length - suf + k;
            result.Add(new DiffLine { Kind = DiffKind.Equal, Text = left[li], LeftLineNo = li + 1, RightLineNo = ri + 1 });
        }
        return AlignSimilarLines(result);
    }

    /// <summary>Myers O(ND): đường biên tập ngắn nhất giữa left[offset..offset+n) và right[offset..offset+m). Trả null nếu cần hơn <paramref name="maxD"/> thay đổi.
    /// Mỗi phần tử: (loại, chỉ số dòng trái, chỉ số dòng phải) tính từ đầu đoạn giữa; Removed chỉ có chỉ số trái, Added chỉ có chỉ số phải.</summary>
    private static List<(DiffKind Kind, int Left, int Right)>? Myers(string[] a, string[] b, int off0, int n, int m, int maxD)
    {
        if (n == 0 && m == 0) return new();
        var max = Math.Min(n + m, maxD);
        var v = new int[2 * max + 3];
        var off = max + 1;
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            var snap = new int[2 * d + 3];                       // v của vòng trước, các đường chéo -d-1 .. d+1
            Array.Copy(v, off - d - 1, snap, 0, 2 * d + 3);
            trace.Add(snap);
            for (var k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[off0 + x] == b[off0 + y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) return Trace(trace, n, m, d);
            }
        }
        return null;
    }

    private static List<(DiffKind Kind, int Left, int Right)> Trace(List<int[]> trace, int n, int m, int dEnd)
    {
        var edits = new List<(DiffKind, int, int)>();
        int x = n, y = m;
        for (var d = dEnd; d > 0; d--)
        {
            var snap = trace[d];
            var k = x - y;
            var prevK = k == -d || (k != d && snap[k - 1 + d + 1] < snap[k + 1 + d + 1]) ? k + 1 : k - 1;
            var prevX = snap[prevK + d + 1];
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY) { x--; y--; edits.Add((DiffKind.Equal, x, y)); }
            if (x == prevX) { y--; edits.Add((DiffKind.Added, -1, y)); }       // đi xuống: chèn dòng right[y]
            else { x--; edits.Add((DiffKind.Removed, x, -1)); }                // đi ngang: xoá dòng left[x]
        }
        while (x > 0 && y > 0) { x--; y--; edits.Add((DiffKind.Equal, x, y)); }
        edits.Reverse();
        return edits;
    }

    /// <summary>LCS trên đoạn giữa (đã bỏ đầu/cuối giống nhau), đi từ đầu tới cuối. Bảng <c>dp[i, j]</c> = độ dài LCS của phần HẬU TỐ left[i..], right[j..] nên
    /// phải đi XUÔI từ (0, 0) — bản cũ đi ngược từ cuối nhưng lại đọc bảng như thể là LCS của phần TIỀN TỐ, nên sai: 2 đoạn chỉ khác 2 ký tự mà báo hàng chục dòng khác.</summary>
    private static void DiffMiddle(string[] left, string[] right, int offset, int n, int m, List<DiffLine> result)
    {
        // Ít thay đổi (phần lớn các lần so sánh): thuật toán Myers — nhanh và bộ nhớ nhỏ kể cả file hàng chục nghìn dòng.
        if (Myers(left, right, offset, n, m, MaxMyersEdits) is { } script)
        {
            foreach (var (kind, li, ri) in script)
                result.Add(kind switch
                {
                    DiffKind.Equal => new DiffLine { Kind = kind, Text = left[offset + li], LeftLineNo = offset + li + 1, RightLineNo = offset + ri + 1 },
                    DiffKind.Removed => new DiffLine { Kind = kind, Text = left[offset + li], LeftLineNo = offset + li + 1 },
                    _ => new DiffLine { Kind = kind, Text = right[offset + ri], RightLineNo = offset + ri + 1 },
                });
            return;
        }

        if ((long)n * m > MaxLcsCells)
        {
            // Quá lớn: so từng dòng theo vị trí (không căn lại khi chèn/xoá cụm) — vẫn đúng là "dòng nào khác thì báo khác".
            var common = Math.Min(n, m);
            for (var k = 0; k < common; k++)
            {
                var l = left[offset + k]; var r = right[offset + k];
                if (l == r) result.Add(new DiffLine { Kind = DiffKind.Equal, Text = l, LeftLineNo = offset + k + 1, RightLineNo = offset + k + 1 });
                else
                {
                    result.Add(new DiffLine { Kind = DiffKind.Removed, Text = l, LeftLineNo = offset + k + 1 });
                    result.Add(new DiffLine { Kind = DiffKind.Added, Text = r, RightLineNo = offset + k + 1 });
                }
            }
            for (var k = common; k < n; k++) result.Add(new DiffLine { Kind = DiffKind.Removed, Text = left[offset + k], LeftLineNo = offset + k + 1 });
            for (var k = common; k < m; k++) result.Add(new DiffLine { Kind = DiffKind.Added, Text = right[offset + k], RightLineNo = offset + k + 1 });
            return;
        }

        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = left[offset + i] == right[offset + j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (left[offset + a] == right[offset + b])
            {
                result.Add(new DiffLine { Kind = DiffKind.Equal, Text = left[offset + a], LeftLineNo = offset + a + 1, RightLineNo = offset + b + 1 });
                a++; b++;
            }
            else if (dp[a + 1, b] >= dp[a, b + 1])
            {
                result.Add(new DiffLine { Kind = DiffKind.Removed, Text = left[offset + a], LeftLineNo = offset + a + 1 });
                a++;
            }
            else
            {
                result.Add(new DiffLine { Kind = DiffKind.Added, Text = right[offset + b], RightLineNo = offset + b + 1 });
                b++;
            }
        }
        for (; a < n; a++) result.Add(new DiffLine { Kind = DiffKind.Removed, Text = left[offset + a], LeftLineNo = offset + a + 1 });
        for (; b < m; b++) result.Add(new DiffLine { Kind = DiffKind.Added, Text = right[offset + b], RightLineNo = offset + b + 1 });
    }

    /// <summary>
    /// LCS chỉ biết "giống hệt hay không": trái "test" / phải "te" + "test" thì nó ghép "test" với "test" ở dòng 2 và coi "te" là dòng chèn thêm,
    /// trong khi người đọc mong so "test" (dòng 1) với "te" (dòng 1) rồi báo lệch chữ "st". Bước chỉnh này tìm chỗ 1 dòng chỉ-có-1-bên nằm ngay trước
    /// 1 dòng Equal, ĐÚNG VỊ TRÍ (cùng số dòng với dòng bên kia) và giống nó ít nhất nửa (đầu + cuối trùng) → ghép chúng thành 1 cặp "sửa", còn
    /// dòng Equal cũ thành dòng chèn/xoá thêm. Chỉ đụng tới đúng vị trí đã thẳng hàng nên không làm hỏng việc căn dòng khi chèn/xoá cả cụm ở giữa file.
    /// </summary>
    private static List<DiffLine> AlignSimilarLines(List<DiffLine> diff)
    {
        var output = new List<DiffLine>(diff.Count + 4);
        for (var i = 0; i < diff.Count; i++)
        {
            var cur = diff[i];
            if (cur.Kind == DiffKind.Equal && output.Count > 0)
            {
                var prev = output[^1];
                var beforePrev = output.Count > 1 ? output[^2] : null;
                // [Added a][Equal e] — a nằm đúng dòng của e bên trái, và a giống e.
                if (prev.Kind == DiffKind.Added && beforePrev?.Kind != DiffKind.Removed
                    && prev.RightLineNo == cur.LeftLineNo && IsSimilar(prev.Text, cur.Text))
                {
                    output.Add(new DiffLine { Kind = DiffKind.Removed, Text = cur.Text, LeftLineNo = cur.LeftLineNo });
                    output.Add(new DiffLine { Kind = DiffKind.Added, Text = cur.Text, RightLineNo = cur.RightLineNo });
                    continue;
                }
                // [Removed a][Equal e] — a nằm đúng dòng của e bên phải, và a giống e.
                if (prev.Kind == DiffKind.Removed && beforePrev?.Kind != DiffKind.Added
                    && prev.LeftLineNo == cur.RightLineNo && IsSimilar(prev.Text, cur.Text))
                {
                    output.Add(new DiffLine { Kind = DiffKind.Removed, Text = cur.Text, LeftLineNo = cur.LeftLineNo });
                    output.Add(new DiffLine { Kind = DiffKind.Added, Text = cur.Text, RightLineNo = cur.RightLineNo });
                    continue;
                }
            }
            output.Add(cur);
        }
        return output;
    }

    /// <summary>Hai dòng "giống nhau" khi phần đầu + phần cuối trùng chiếm ít nhất nửa độ dài dòng dài hơn (và không rỗng).</summary>
    private static bool IsSimilar(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        var pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        var suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
        return (pre + suf) * 2 >= Math.Max(a.Length, b.Length);
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
}
