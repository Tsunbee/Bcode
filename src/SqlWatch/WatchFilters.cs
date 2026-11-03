namespace SqlWatch;

/// <summary>Bộ lọc của 1 dự án (Login chứa, USER ID, Nội dung chứa, các Sự kiện đang bật) — nhớ theo dự án;
/// chạy riêng thì nhớ trong phiên, Bcode nhúng thì lưu vào workspace (MainForm.FiltersLookup / FiltersSaved).</summary>
public sealed record WatchFilters(string Login, string UserId, string Text, IReadOnlyList<string> Events);
