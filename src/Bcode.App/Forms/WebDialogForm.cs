using System.Text.Json;
using Bcode.App.Controls;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Nền chung cho các hộp thoại / form dạng WebView2: một trang HTML (Web/Shell/*.html, dùng chung shell.css + dialog.css nên tự co giãn theo cỡ cửa sổ
/// và ăn theo Template giao diện) lấp đầy form, nói chuyện với C# bằng JSON <c>{action, ...}</c> (WebBarHost lo nạp trang, đẩy theme, nhận tin).
/// Lớp con chỉ cần: <see cref="OnReady"/> (đẩy dữ liệu ban đầu xuống trang) và <see cref="OnActionAsync"/> (xử lý từng action trang gửi lên);
/// dùng <see cref="Js"/> để gọi hàm của trang, <see cref="CloseWith"/> để đóng kèm kết quả. Phần "dữ liệu" của lớp con giữ nguyên như form WinForms cũ.
/// </summary>
public abstract class WebDialogForm : ThemedForm
{
    protected static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    protected readonly WebBarHost Web;

    protected WebDialogForm(string title, string page, int width, int height, int minWidth = 420, int minHeight = 260, bool resizable = true)
    {
        Text = title;
        Width = width;
        Height = height;
        MinimumSize = new Size(minWidth, minHeight);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = resizable;
        ShowIcon = false;
        KeyPreview = true;

        Web = new WebBarHost(page) { Dock = DockStyle.Fill };
        Controls.Add(Web);
        Web.Ready += () => OnReady();
        Web.Message += root =>
        {
            var copy = root.Clone();                                    // JsonElement chỉ hợp lệ trong handler — copy trước khi xử lý bất đồng bộ
            var action = copy.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            _ = DispatchAsync(action, copy);
        };
    }

    /// <summary>Trang đã nạp xong: đẩy dữ liệu ban đầu (gọi hàm JS của trang bằng <see cref="Js"/>).</summary>
    protected abstract void OnReady();

    /// <summary>Xử lý một action trang gửi lên. "close" / "cancel" đã được xử lý sẵn (đóng form, DialogResult.Cancel).</summary>
    protected abstract Task OnActionAsync(string action, JsonElement msg);

    private async Task DispatchAsync(string action, JsonElement msg)
    {
        try
        {
            if (action is "close" or "cancel") { CloseWith(DialogResult.Cancel); return; }
            await OnActionAsync(action, msg);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected static string J(object? value) => JsonSerializer.Serialize(value, JsonOpts);

    /// <summary>Chạy một lệnh JS ở trang (an toàn từ luồng nền).</summary>
    protected void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => Web.Call(script)); } catch { /* đang đóng */ } }
        else Web.Call(script);
    }

    protected void CloseWith(DialogResult result)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => CloseWith(result)); } catch { /* đang đóng */ } return; }
        DialogResult = result;
        Close();
    }
}
