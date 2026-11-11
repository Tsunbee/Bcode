using System.Text.Json;
using Bcode.Shared;

namespace Bcode.App.Forms;

/// <summary>
/// Hộp "Cập nhật Bcode" (Settings → Kiểm tra bản Bcode mới; Web/Shell/update.html). Xử lý nằm ở Shared/AppUpdateDialog.cs (dùng chung với BcodeViewer),
/// form này chỉ nạp trang + chuyển action. Việc cài do MainForm làm lúc đóng (MainForm.Update.cs) qua <see cref="UpdateCheckService.StartApply"/>.
/// </summary>
public sealed class UpdateForm : WebDialogForm
{
    private readonly UpdateDialogLogic _logic;

    public UpdateDialogLogic.Outcome Decision => _logic.Decision;
    public UpdatePlan? Plan => _logic.Plan;
    public BackupInfo? Backup => _logic.Backup;
    public bool OverwriteModified => _logic.OverwriteModified;

    /// <param name="folder">Thư mục phát hành đang dùng (đọc lại mỗi lần kiểm tra).</param>
    /// <param name="chooseFolder">Mở hộp đổi thư mục phát hành; true = đã đổi.</param>
    /// <param name="pendingOnClose">Đã hẹn cài khi đóng Bcode (hiện nhắc + cho huỷ hẹn).</param>
    public UpdateForm(Func<string> folder, Func<bool> chooseFolder, bool pendingOnClose)
        : base("Cập nhật Bcode", "update.html", 680, 640, 480, 420)
    {
        _logic = new UpdateDialogLogic(folder, chooseFolder, pendingOnClose, Js, CloseWith, () => IsDisposed);
        FormClosing += (_, _) => _logic.Cancel();
    }

    protected override void OnReady() => _logic.Ready();

    protected override Task OnActionAsync(string action, JsonElement msg) => _logic.HandleAsync(action, msg);
}
