using System.Text.Json;
using Bcode.App.Forms;
using Bcode.App.Services;

namespace BcodeNote.App;

/// <summary>Hộp "Gợi ý theo menu" (notesuggest.html): liệt kê controller / file / SQL Object / table của menu, tick sẵn theo
/// <see cref="GenAllService.SuggestForMenuAsync"/>; người dùng tick lại rồi "Điền vào y/c" → <see cref="Pick"/>.</summary>
internal sealed class MenuSuggestForm : WebDialogForm
{
    private readonly MenuSuggestion _s;
    private readonly string _project;

    public MenuSuggestionPick? Pick { get; private set; }

    public MenuSuggestForm(MenuSuggestion s, string project)
        : base("Gợi ý theo menu — " + s.Menu, "notesuggest.html", 1100, 760, 760, 480)
    {
        _s = s;
        _project = project;
    }

    protected override void OnReady() => Js($"sug.show({J(new { project = _project, s = _s })})");

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        if (action == "apply" && msg.TryGetProperty("pick", out var p))
        {
            Pick = p.Deserialize<MenuSuggestionPick>(JsonOpts);
            CloseWith(DialogResult.OK);
        }
        return Task.CompletedTask;
    }
}
