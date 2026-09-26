namespace Bcode.App.Models;

public class ApiConfigItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectId { get; set; } = "";
    public string Department { get; set; } = ""; // Phòng xử lý
    public string BaseApiUrl { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string TokenRequestBody { get; set; } = "{\n  \"username\": \"admin\",\n  \"password\": \"123\"\n}";
    public string CurrentToken { get; set; } = "";
    public string SyncType { get; set; } = "Danh mục"; // "Danh mục" hoặc "Chứng từ"
    public string SchemaFilePath { get; set; } = ""; // File cấu trúc đính kèm
}