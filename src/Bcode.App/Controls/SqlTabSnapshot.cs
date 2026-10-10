namespace Bcode.App.Controls;

/// <summary>Trạng thái 1 tab SQL Query lúc "ngủ đông" (Chế độ hiệu năng): đủ để dựng lại tab y như cũ sau khi giải phóng WebView2 —
/// trừ lịch sử Undo của editor. Xem <see cref="RawSqlControl.TryCaptureSnapshotAsync"/> / <see cref="RawSqlControl.RestoreSnapshot"/>.</summary>
public sealed record SqlTabSnapshot(
    string Text,
    bool Sys,
    string ViewState,
    bool WordWrap,
    bool Suggest,
    bool ResetConn,
    bool ResultTab,
    bool ResultsCollapsed,
    int? SplitDistance,
    string? FilePath,
    string? LastScript,
    string StatusText,
    Color StatusColor,
    SqlResultTabs.Snapshot Results);
