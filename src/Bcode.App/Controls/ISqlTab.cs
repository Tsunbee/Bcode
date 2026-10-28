using System.Data;
using Bcode.App.Models;

namespace Bcode.App.Controls;

/// <summary>Tab "SQL Query" — hai bản cài đặt: <see cref="RawSqlControl"/> (Monaco/WebView2) và <see cref="NativeSqlControl"/> (Scintilla + lưới WinForms, chế độ App).
/// MainForm chỉ nói chuyện qua giao diện này nên hai chế độ dùng chung toàn bộ phần mở tab / phiên / ngủ đông / mở object. Cả hai đều là <see cref="Control"/>
/// (IsDisposed / Dock / Dispose do Control cung cấp); khi cần đưa vào TabPage thì ép kiểu <c>(Control)tab</c>.</summary>
internal interface ISqlTab
{
    bool IsDisposed { get; }
    DockStyle Dock { get; set; }
    void Dispose();

    /// <summary>Editor đã nạp xong, gõ được ngay.</summary>
    bool IsReady { get; }
    /// <summary>Tab dựng sẵn bắt đầu được dùng thật (từ lúc này mới nạp danh sách bảng...).</summary>
    void BeginUse();
    bool UseSysDatabase { get; }
    void SetDatabase(bool useSysDatabase);
    /// <summary>Nút Refresh database: bỏ danh sách bảng / cột / procedure đã nhớ cho gợi ý code và nạp lại từ database.</summary>
    void RefreshSchema();
    void FocusEditor();

    Task<string> GetScriptTextAsync();
    Task<string> GetSelectedTextAsync();
    Task<long> GetEditorVersionAsync();
    void SetScriptText(string text);
    Task SetScriptTextAsync(string text);
    Task OpenScriptAsync(string text);
    Task PrependDefinitionAsync(string definition);
    Task InsertSnippetAsync(string text);
    Task LoadAndDebugAsync(string definition, string? callText);

    Task<SqlTabSnapshot?> TryCaptureSnapshotAsync();
    void RestoreSnapshot(SqlTabSnapshot snapshot);

    /// <summary>Hiện khung Claude/Gemini nhúng bên phải tab.</summary>
    AiWebPanel ShowAi(Bcode.Shared.AiSite site);
    /// <summary>Hiện "Hỏi Fast" nhúng bên phải tab, cùng khung với Claude / Gemini (<paramref name="panel"/> = FastAskControl MainForm tạo riêng cho tab này).</summary>
    void ShowFastAsk(Control panel);
    /// <summary>Bấm "Hỏi Fast" ở thanh đầu khung nhúng khi tab chưa có khung Hỏi Fast → MainForm tạo rồi gọi <see cref="ShowFastAsk"/>.</summary>
    event Action? FastAskRequested;

    event Action<DataTable>? ResultReady;
    event Action<string>? CreateRptRequested;
    event Action<string, bool, bool, int, int>? ScriptExecuted;
    event Action<string, bool>? SaveHistoryRequested;
    Func<string>? CurrentProject { get; set; }
    event Action<List<DataTable>, string>? OpenResultInNewTabRequested;
    event Action<string, bool, string>? OpenProcedureWithQueryRequested;
    event Action<string, bool, string>? OpenObjectInNewTabRequested;
    event Action<SqlObjectInfo, string?>? DebugTargetChosen;
    event Action<string, string>? AskAiRequested;
}

internal static class SqlTabExtensions
{
    /// <summary>Tab SQL luôn là một <see cref="Control"/> — dùng khi cần đưa vào TabPage / ThemeManager.</summary>
    public static Control AsControl(this ISqlTab tab) => (Control)tab;
}
