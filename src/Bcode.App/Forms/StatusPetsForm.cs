using System.Text.Json;
using Bcode.App.Models;

namespace Bcode.App.Forms;

/// <summary>Cửa sổ "Thú cưng trên thanh trạng thái" (Web/Shell/statuspets.html; chế độ App: Controls/NativePages.StatusPets.cs).
/// Mỗi lần đổi gọi <c>preview</c> để thanh trạng thái áp ngay; OK → <see cref="Result"/>, Hủy → bên gọi trả lại cài đặt cũ.</summary>
public sealed class StatusPetsForm : WebDialogForm
{
    private readonly Action<StatusPetSettings> _preview;

    public StatusPetSettings Result { get; private set; }

    public StatusPetsForm(StatusPetSettings current, Action<StatusPetSettings> preview)
        : base("Thú cưng trên thanh trạng thái", "statuspets.html", 640, 600, 480, 460)
    {
        Result = current.Clone().Normalize();
        _preview = preview;
    }

    protected override void OnReady() =>
        Js($"pets.init({J(new
        {
            settings = Result,
            kinds = StatusPetSettings.Kinds.Select(k => new { key = k.Key, label = k.Label, hint = k.Hint }),
            max = StatusPetSettings.MaxPerKind,
        })})");

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
