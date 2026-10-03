using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>
/// Line-based diff for the "Compare Text" tool. Implements a straightforward
/// LCS (longest common subsequence) diff — good enough for comparing script
/// files up to a few thousand lines; no external diff library needed.
/// </summary>
public class DiffService
{
    public List<DiffLine> Diff(string leftText, string rightText)
    {
        var left = SplitLines(leftText);
        var right = SplitLines(rightText);

        var lcs = ComputeLcsLengths(left, right);
        var result = new List<DiffLine>();
        Backtrack(lcs, left, right, left.Length, right.Length, result);
        result.Reverse();
        return AlignSimilarLines(result);
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

    private static int[,] ComputeLcsLengths(string[] left, string[] right)
    {
        var dp = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                dp[i, j] = left[i] == right[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }
        return dp;
    }

    private static void Backtrack(int[,] dp, string[] left, string[] right, int i, int j, List<DiffLine> result)
    {
        while (i > 0 && j > 0)
        {
            if (left[i - 1] == right[j - 1])
            {
                result.Add(new DiffLine { Kind = DiffKind.Equal, Text = left[i - 1], LeftLineNo = i, RightLineNo = j });
                i--; j--;
            }
            else if (dp[i - 1, j] >= dp[i, j - 1])
            {
                result.Add(new DiffLine { Kind = DiffKind.Removed, Text = left[i - 1], LeftLineNo = i });
                i--;
            }
            else
            {
                result.Add(new DiffLine { Kind = DiffKind.Added, Text = right[j - 1], RightLineNo = j });
                j--;
            }
        }
        while (i > 0)
        {
            result.Add(new DiffLine { Kind = DiffKind.Removed, Text = left[i - 1], LeftLineNo = i });
            i--;
        }
        while (j > 0)
        {
            result.Add(new DiffLine { Kind = DiffKind.Added, Text = right[j - 1], RightLineNo = j });
            j--;
        }
    }
}
