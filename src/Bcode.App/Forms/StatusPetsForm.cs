using System.Text.Json;
using Bcode.App.Models;

namespace Bcode.App.Forms;

/// <summary>Cửa sổ "Thú cưng (thanh trạng thái, SQL Query)" (Web/Shell/statuspets.html; chế độ App: Controls/NativePages.StatusPets.cs).
/// Mỗi lần đổi gọi <c>preview</c> để thanh trạng thái + editor SQL Query áp ngay; OK → <see cref="Result"/>, Hủy → bên gọi trả lại cài đặt cũ.</summary>
public sealed class StatusPetsForm : WebDialogForm
{
    private readonly Action<StatusPetSettings> _preview;
    private readonly IReadOnlyList<string> _workspaces;
    private readonly string? _currentWorkspace;

    public StatusPetSettings Result { get; private set; }

    /// <param name="workspaces">Tên các workspace — để đánh dấu workspace nào là DB thật của khách.</param>
    /// <param name="currentWorkspace">Workspace đang chọn (ghi "đang chọn" cạnh tên).</param>
    public StatusPetsForm(StatusPetSettings current, Action<StatusPetSettings> preview, IReadOnlyList<string>? workspaces = null, string? currentWorkspace = null)
        : base("Thú cưng (thanh trạng thái, SQL Query)", "statuspets.html", 700, 860, 480, 480)
    {
        Result = current.Clone().Normalize();
        _preview = preview;
        _workspaces = workspaces ?? Array.Empty<string>();
        _currentWorkspace = currentWorkspace;
    }

    /// <summary>Dữ liệu dựng cửa sổ (dùng chung cho bản Web — pets.init — và bản App — NativePages.StatusPets.cs).</summary>
    internal static object InitData(StatusPetSettings s, IReadOnlyList<string> workspaces, string? current) => new
    {
        settings = s,
        kinds = StatusPetSettings.Kinds.Select(k => new { key = k.Key, label = k.Label, hint = k.Hint, name = StatusPetSettings.DefaultNames.GetValueOrDefault(k.Key, k.Key) }),
        editorKinds = StatusPetSettings.EditorKinds,
        max = StatusPetSettings.MaxPerKind,
        styles = StatusPetSettings.EditorStyles.Select(x => new { key = x.Key, label = x.Label }),
        rest = StatusPetSettings.RestChoices,
        seasons = StatusPetSettings.SeasonModes.Select(x => new { key = x.Key, label = x.Label }),
        workspaces,
        current,
        env = new { season = StatusPetEnv.SeasonOf(DateTime.Now), night = StatusPetEnv.IsNight(DateTime.Now) },
    };

    protected override void OnReady() => Js($"pets.init({J(InitData(Result, _workspaces, _currentWorkspace))})");

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        if (action is "change" or "ok" && msg.TryGetProperty("value", out var v))
        {
            try { Result = (JsonSerializer.Deserialize<StatusPetSettings>(v.GetRawText(), JsonOpts) ?? Result).Normalize(); } catch { /* giữ giá trị trước */ }
            _preview(Result);
            if (action == "ok") CloseWith(DialogResult.OK);
        }
        return Task.CompletedTask;
    }
}
