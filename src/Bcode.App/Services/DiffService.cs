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
        return result;
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
