using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>Tab "Hướng dẫn gợi ý code SQL" (Settings): bảng gõ-gì-ra-gì. Trang WebView2 (Web/Shell/sqlhints-help.html); dữ liệu sinh từ <see cref="SqlHintCatalog"/> nên luôn khớp với editor.</summary>
public class SqlHintsHelpControl : UserControl
{
    private readonly WebBarHost _web = new("sqlhints-help.html") { Dock = DockStyle.Fill };

    public SqlHintsHelpControl()
    {
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Ready += () => _web.Call($"help.init({JsonSerializer.Serialize(SqlHintCatalog.ToHelpModel())})");
    }
}
