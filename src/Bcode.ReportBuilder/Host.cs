using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Bcode.ReportBuilder;

/// <summary>Một trang HTML (WebView2) do Bcode dựng sẵn cho module: module chỉ gửi script / nhận tin nhắn, không tự khởi tạo WebView2.</summary>
public interface IWebPage
{
    /// <summary>Control cần đặt vào giao diện (Dock = Fill do module đặt).</summary>
    Control View { get; }
    /// <summary>Trang gửi tin nhắn (JSON object) — chỉ hợp lệ trong lúc xử lý.</summary>
    event Action<JsonElement>? Message;
    /// <summary>Trang đã nạp xong (đã đẩy theme) — từ đây mới gọi <see cref="Call"/> được.</summary>
    event Action? Ready;
    void Call(string script);
}

/// <summary>Những gì module cần từ Bcode (module tự đứng độc lập, không tham chiếu Bcode.exe — giống ExcelToFrx / SqlDecryptor.Core).</summary>
public interface IReportHost
{
    /// <summary>Kết nối tới database của workspace hiện tại: false = App Data, true = Sys Data.</summary>
    SqlConnection CreateConnection(bool useSysDatabase);
    string WorkspaceName { get; }
    /// <summary>Source Path của workspace hiện tại (thư mục source Fast); rỗng nếu chưa khai.</summary>
    string SourcePath { get; }
    /// <summary>Thư mục %AppData% của Bcode (chứa bản nháp, backup).</summary>
    string AppDataDir { get; }
    /// <summary>Thư mục source mẫu của "Tạo báo cáo" (<c>Templates\fileSource\BuildReport</c> cạnh Bcode.exe): khung procedure và các mảnh ghép nhóm / xoay — không có T-SQL khung cố định trong code.</summary>
    string TemplateDir { get; }
    /// <summary>Tạo trang web từ nội dung HTML (Bcode ghi ra Web\Shell nếu cần rồi nạp bằng WebView2 dùng chung, có theme).</summary>
    IWebPage CreatePage(string fileName, string html);
    /// <summary>Menu hiện có của chương trình (dùng bản lưu của Bcode nên gần như tức thì).</summary>
    Task<List<MenuRow>> GetMenuAsync();
}

/// <summary>Một dòng menu của chương trình (từ bản lưu menu của Bcode — không phải đọc lại database).</summary>
public sealed record MenuRow(string Id, string ParentId, string Title, string TitleEn, string Link, string SysId);

/// <summary>Môi trường dùng chung trong module (đặt một lần ở <see cref="Entry.Create"/>).</summary>
internal static class ReportBuilderEnv
{
    public static string AppData { get; set; } = Path.GetTempPath();
    /// <summary>Thư mục file mẫu source (<see cref="IReportHost.TemplateDir"/>).</summary>
    public static string TemplateDir { get; set; } = "";
}
