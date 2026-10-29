using Bcode.App.Services;
using Microsoft.Data.SqlClient;

namespace Bcode.App.UI;

/// <summary>Lệnh "Học gợi ý Fast từ database này" dùng chung cho editor SQL Web (RawSqlControl) và App (NativeSqlControl).</summary>
public static class FastGhostLearnUi
{
    /// <summary>Hỏi xác nhận → đọc procedure/function của database (chỉ SELECT sys.sql_modules) → học + hiệu chỉnh ma trận → báo độ chính xác đo được.</summary>
    public static async Task RunAsync(Control owner, string dbKey, Func<SqlConnection> connect)
    {
        var path = FastGhostRules.LearnedPath(dbKey);
        var again = File.Exists(path) ? "\n\nĐã học trước đó — lần này sẽ học lại và ghi đè." : "";
        if (MessageBox.Show(owner,
                "Học gợi ý Fast (ghost text) từ toàn bộ procedure / function của database đang kết nối?\n\n" +
                "• Chỉ ĐỌC định nghĩa (SELECT sys.sql_modules) — không ghi gì vào database.\n" +
                "• Tự đo trên 1 nửa số store chưa dùng để học, chọn ngưỡng để gợi ý đúng ≥ 90% (mất 3–5 phút).\n" +
                "• Kết quả lưu riêng cho database này, tra trước ma trận đi kèm Bcode." + again,
                "Bcode — Học gợi ý Fast", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        owner.UseWaitCursor = true;
        try
        {
            var res = await FastGhostMiner.LearnFromDatabaseAsync(dbKey, connect);
            MessageBox.Show(owner, res.Summary() + $"\nLưu tại: {path}", "Bcode — Học gợi ý Fast", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Không học được: " + ex.Message, "Bcode — Học gợi ý Fast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { owner.UseWaitCursor = false; }
    }
}
