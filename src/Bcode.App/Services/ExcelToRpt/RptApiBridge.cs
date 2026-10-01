using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace Bcode.App.Services.ExcelToRpt;

/// <summary>Trả lời một request /api/* của giao diện (thân JSON hoặc file).</summary>
internal sealed record ApiResponse(int Status, string ContentType, byte[] Body, string? DownloadName = null)
{
    public static ApiResponse Json(int status, JsonNode body) =>
        new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body.ToJsonString(LayoutJson.Compact)));

    public static ApiResponse Error(int status, string error, string? log = null)
    {
        var o = new JsonObject { ["error"] = error };
        if (log is not null) o["log"] = log;
        return Json(status, o);
    }

    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Thay webapp/server.py: nhận đúng các request /api/* mà index.html gửi, xử lý bằng C#.
///
/// Không mở cổng HTTP, không cookie/phiên: mỗi tab "Excel → RPT" có một bridge và một thư
/// mục làm việc riêng (%TEMP%\Bcode\ExcelToRpt\&lt;guid&gt;) — đúng vai trò "phiên" của server.py.
/// </summary>
internal sealed class RptApiBridge : IDisposable
{
    private readonly RptGeneratorRunner _generator;
    private readonly TemplateLocator _templates;
    private readonly Func<string> _defaultOutDir;
    private readonly Action<string> _rememberOutDir;

    // Trạng thái phiên (server.py: new_state())
    private string? _template, _xsd, _rptXml, _form, _style;

    public string WorkDir { get; }

    public RptApiBridge(RptGeneratorRunner generator, TemplateLocator templates,
        Func<string> defaultOutDir, Action<string> rememberOutDir)
    {
        _generator = generator;
        _templates = templates;
        _defaultOutDir = defaultOutDir;
        _rememberOutDir = rememberOutDir;
        WorkDir = Path.Combine(Path.GetTempPath(), "Bcode", "ExcelToRpt", Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(WorkDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(WorkDir, recursive: true); } catch { /* file đang mở trong trình xem PDF... bỏ qua */ }
    }

    /// <summary>Đường dẫn file được phép đọc qua /api/file: trong thư mục phiên hoặc thư mục template.</summary>
    public bool IsReadable(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        if (!File.Exists(full)) return false;
        var allowed = new List<string> { WorkDir };
        allowed.AddRange(_templates.Folders);
        return allowed.Any(a =>
        {
            try
            {
                var root = Path.GetFullPath(a).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        });
    }

    public async Task<ApiResponse> HandleAsync(string method, string pathAndQuery, byte[] body)
    {
        var qi = pathAndQuery.IndexOf('?');
        var path = qi >= 0 ? pathAndQuery[..qi] : pathAndQuery;
        var q = HttpUtility.ParseQueryString(qi >= 0 ? pathAndQuery[(qi + 1)..] : "");
        try
        {
            if (method == "GET")
                return path == "/api/file" ? ApiFile(q["p"] ?? "", q["dl"] == "1") : ApiResponse.Error(404, "not found");

            return path switch
            {
                "/api/parse" => ApiParse(q["name"] ?? "input.xlsx", body),
                "/api/template" => ApiTemplate(q["name"] ?? "template.rpt", body),
                "/api/xsd" => ApiXsd(q["name"] ?? "schema.xsd", body),
                "/api/reportxml" => ApiReportXml(q["name"] ?? "report.xml", q["form"] ?? "", body),
                "/api/style" => await ApiStyleAsync(q["name"] ?? "style.rpt", body),
                "/api/fields" => await ApiFieldsAsync(),
                "/api/generate" => await ApiGenerateAsync(body),
                _ => ApiResponse.Error(404, "not found"),
            };
        }
        catch (Exception ex)
        {
            return ApiResponse.Json(500, new JsonObject { ["error"] = ex.Message, ["trace"] = ex.ToString() });
        }
    }

    /// <summary>Chỉ lấy tên file — chặn path traversal ("..\..\x").</summary>
    private string SaveUpload(string name, byte[] body, string prefix = "")
    {
        var safe = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(safe)) safe = "upload.bin";
        var p = Path.Combine(WorkDir, prefix + safe);
        File.WriteAllBytes(p, body);
        return p;
    }

    private ApiResponse ApiFile(string p, bool download)
    {
        if (!IsReadable(p)) return ApiResponse.Error(404, "not found");
        var full = Path.GetFullPath(p);
        var ctype = full.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !download
            ? "application/pdf" : "application/octet-stream";
        return new ApiResponse(200, ctype, File.ReadAllBytes(full), download ? Path.GetFileName(full) : null);
    }

    // --- upload .xlsx và parse ---------------------------------------------
    private ApiResponse ApiParse(string name, byte[] body)
    {
        var xlsx = SaveUpload(name, body);
        var stem = Path.GetFileNameWithoutExtension(xlsx);

        JsonObject layout;
        string log;
        try
        {
            var result = ExcelLayoutParser.Convert(xlsx);
            layout = result.Layout;
            log = result.Log;
            File.WriteAllText(Path.Combine(WorkDir, stem + ".layout.json"),
                layout.ToJsonString(LayoutJson.Options), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            return ApiResponse.Error(400, "Parse that bai", ex.Message);
        }

        // Tự tìm template .rpt cùng tên
        var auto = _templates.FindByStem(stem, WorkDir);
        if (auto is not null) _template = auto;

        // KHÔNG dùng lại template của file trước: mỗi báo cáo có bộ trường riêng, dùng nhầm
        // template thì mọi trường không khớp thành placeholder [!2.ten_vt] trong .rpt
        var old = _template;
        if (auto is null && old is not null &&
            !string.Equals(Path.GetFileNameWithoutExtension(old), stem, StringComparison.OrdinalIgnoreCase))
            _template = null;

        var usedDefault = false;
        if (auto is null && _template is null)
        {
            var dflt = _templates.FindDefault();
            if (dflt is not null)
            {
                _template = dflt;
                usedDefault = true;
                log += $"\nDung template MAC DINH '{Path.GetFileName(dflt)}' vi khong co file '{stem}.rpt'.";
                log += "\n  Luu y: bo truong cua template nay khac bao cao cua ban, nen hay chon them file .xsd " +
                       "de nap dung DataSet - neu khong moi truong se thanh [!N.ten].";
            }
            else if (old is not null)
                log += $"\nCHU Y: da bo template cu '{Path.GetFileName(old)}' vi khong phai cua bao cao nay. " +
                       $"Hay chon dung file '{stem}.rpt'.";
        }

        return ApiResponse.Json(200, new JsonObject
        {
            ["layout"] = layout,
            ["log"] = log,
            ["template"] = _template,
            ["template_is_default"] = usedDefault,
        });
    }

    // --- upload template .rpt ------------------------------------------------
    private ApiResponse ApiTemplate(string name, byte[] body)
    {
        _template = SaveUpload(name, body);
        return ApiResponse.Json(200, new JsonObject { ["template"] = _template });
    }

    // --- upload .xsd ------------------------------------------------------------
    private ApiResponse ApiXsd(string name, byte[] body)
    {
        var p = SaveUpload(name, body);
        _xsd = p;
        JsonObject data;
        try { data = XsdFieldReader.Parse(p); }
        catch (Exception ex) { return ApiResponse.Error(400, "Khong doc duoc .xsd", ex.Message); }
        data["path"] = p;
        data["from"] = "xsd";
        return ApiResponse.Json(200, data);
    }

    // --- upload .xml định nghĩa report (tham số + chữ tiếng Việt) ----------
    private ApiResponse ApiReportXml(string name, string form, byte[] body)
    {
        var p = Path.Combine(WorkDir, Path.GetFileName(name));
        if (body.Length > 0)                    // rỗng = chỉ đổi form của file đã nạp
        {
            p = SaveUpload(name, body);
            _rptXml = p;
        }
        p = _rptXml ?? p;
        _form = string.IsNullOrEmpty(form) ? null : form;

        JsonObject data;
        try { data = ReportXmlReader.Parse(p, _form); }
        catch (Exception ex) { return ApiResponse.Error(400, "Khong doc duoc .xml", ex.Message); }
        data["path"] = p;
        return ApiResponse.Json(200, data);
    }

    /// <summary>Bóc khối JSON trong output của RptGenerator (từ '{' đầu tới '}' cuối).</summary>
    private static JsonObject? ExtractJson(string output, bool toEnd = false)
    {
        var a = output.IndexOf('{');
        var b = toEnd ? output.Length - 1 : output.LastIndexOf('}');
        if (a < 0 || b < a) return null;
        try { return JsonNode.Parse(output[a..(b + 1)]) as JsonObject; }
        catch (JsonException) { return null; }
    }

    // --- upload .rpt mẫu để lấy quy cách trình bày -------------------------
    private async Task<ApiResponse> ApiStyleAsync(string name, byte[] body)
    {
        var p = SaveUpload(name, body, "style_");
        _style = p;
        if (!_generator.Exists)
            return ApiResponse.Json(200, new JsonObject { ["style"] = p, ["note"] = "Chua co RptGenerator.exe, chua doc duoc quy cach" });
        var (_, output) = await _generator.RunAsync("--inspect", p);
        var data = ExtractJson(output);
        if (data is null) return ApiResponse.Error(400, "Khong doc duoc mau .rpt", output);
        data["style_path"] = p;
        return ApiResponse.Json(200, data);
    }

    // --- liệt kê trường có thật trong template --------------------------------
    private async Task<ApiResponse> ApiFieldsAsync()
    {
        // Có .xsd thì dùng luôn — nhanh và không cần Crystal
        if (_xsd is not null && File.Exists(_xsd))
        {
            try
            {
                var data = XsdFieldReader.Parse(_xsd);
                data["from"] = "xsd";
                return ApiResponse.Json(200, data);
            }
            catch { /* xsd hỏng -> thử template */ }
        }
        if (_template is null) return ApiResponse.Error(400, "Chua chon template .rpt");
        if (!_generator.Exists) return ApiResponse.Error(400, string.Format(RptGeneratorRunner.MissingExeMessage, _generator.ExePath));

        var (_, output) = await _generator.RunAsync("--fields", _template);
        var parsed = ExtractJson(output, toEnd: true);
        return parsed is null ? ApiResponse.Error(400, "Khong doc duoc truong", output) : ApiResponse.Json(200, parsed);
    }

    /// <summary>Đường dẫn file trùng tên đã có trong thư mục lưu, hoặc null (thư mục lưu chính là
    /// thư mục phiên thì không tính — đó là bản của chính tab này).</summary>
    private string? ExistingOutput(string outDir, string outName)
    {
        if (outDir.Length == 0) return null;
        try
        {
            var dest = Path.GetFullPath(Path.Combine(outDir, outName));
            if (string.Equals(Path.GetDirectoryName(dest), Path.GetFullPath(WorkDir), StringComparison.OrdinalIgnoreCase))
                return null;
            return File.Exists(dest) ? dest : null;
        }
        catch { return null; }
    }

    // --- sinh .rpt (+ PDF xem trước) -----------------------------------------
    private async Task<ApiResponse> ApiGenerateAsync(byte[] body)
    {
        var req = JsonNode.Parse(Encoding.UTF8.GetString(body)) as JsonObject
                  ?? throw new InvalidDataException("Request /api/generate khong hop le");
        var layout = req["layout"] as JsonObject ?? throw new InvalidDataException("Thieu layout");
        var wantPdf = req["pdf"] is not JsonValue pv || !pv.TryGetValue<bool>(out var b) || b;

        var stem = (string?)layout["report_name"];
        if (string.IsNullOrWhiteSpace(stem)) stem = "report";
        stem = Path.GetFileName(stem);

        // Nơi lưu: người dùng chỉ định (kể cả đường dẫn mạng tới thư mục Rpt của phần mềm)
        var typedOutDir = ((string?)req["out_dir"] ?? "").Trim();
        var outDir = typedOutDir.Length > 0 ? typedOutDir : _defaultOutDir();
        var outName = ((string?)req["out_name"] ?? "").Trim();
        if (outName.Length == 0) outName = stem + ".generated.rpt";
        if (!outName.EndsWith(".rpt", StringComparison.OrdinalIgnoreCase)) outName += ".rpt";
        outName = Path.GetFileName(outName);

        // KHÔNG ghi đè file .rpt đã có trong thư mục lưu: thư mục Rpt của workspace là file
        // đang chạy thật của dự án, đè nhầm là mất mẫu in cũ. "Tạo file .rpt" dừng hẳn và báo
        // lỗi; "Xem trước PDF" vẫn dựng bình thường nhưng không chép sang thư mục lưu.
        var existing = ExistingOutput(outDir, outName);
        if (existing is not null && !wantPdf)
            return ApiResponse.Json(200, new JsonObject
            {
                ["ok"] = false,
                ["exists"] = existing,
                ["error"] = $"đã có file \"{outName}\" trong thư mục lưu ({outDir}) — không ghi đè. " +
                            "Đổi tên ở ô tên file .rpt, hoặc tự xoá/đổi tên file cũ rồi tạo lại.",
            });

        // Chưa có khuôn -> thử template MẶC ĐỊNH ngay tại đây (người dùng có thể đã bỏ template)
        var fellBack = false;
        if (_template is null && _templates.FindDefault() is { } dflt)
        {
            _template = dflt;
            fellBack = true;
        }
        if (_template is null && _xsd is null)
            return ApiResponse.Error(400,
                "Chua co template .rpt va cung chua co schema .xsd, va cung khong tim thay template mac dinh " +
                $"'{_templates.DefaultName}' trong: {string.Join("; ", _templates.Folders)}. Can it nhat mot trong ba.");
        if (!_generator.Exists)
            return ApiResponse.Error(400, string.Format(RptGeneratorRunner.MissingExeMessage, _generator.ExePath));

        var jsonPath = Path.Combine(WorkDir, stem + ".layout.json");
        File.WriteAllText(jsonPath, layout.ToJsonString(LayoutJson.Options), new UTF8Encoding(false));

        // Sinh vào THƯ MỤC PHIÊN trước rồi mới chép sang nơi lưu: đường dẫn mạng lỗi thì file
        // vẫn còn để lưu tay
        var rptOut = Path.Combine(WorkDir, outName);
        var args = new List<string> { jsonPath, rptOut };
        if (_template is not null) args.Add(_template);

        var used = new JsonObject
        {
            ["template"] = _template is not null ? Path.GetFileName(_template) + (fellBack ? " (MAC DINH)" : "") : "TAO MOI TU DAU",
            ["xsd"] = null, ["rptxml"] = null, ["style"] = null, ["form"] = _form,
        };
        if (_style is not null && File.Exists(_style))
        {
            args.AddRange(new[] { "--style", _style });
            used["style"] = Path.GetFileName(_style);
        }
        if (_xsd is not null && File.Exists(_xsd))
        {
            args.AddRange(new[] { "--xsd", _xsd });
            used["xsd"] = Path.GetFileName(_xsd);
        }

        // Chữ tiếng Việt của từng tham số -> PDF xem trước hiện đúng như bản in
        if (_rptXml is not null && File.Exists(_rptXml))
        {
            try
            {
                var d2 = ReportXmlReader.Parse(_rptXml, _form);
                var pmap = new JsonObject();
                foreach (var p in d2["parameters"]!.AsArray())
                    if (p is JsonObject po && (string?)po["v"] is { Length: > 0 } v)
                        pmap[(string)po["name"]!] = v;
                if (pmap.Count > 0)
                {
                    var pfile = Path.Combine(WorkDir, "params.json");
                    File.WriteAllText(pfile, pmap.ToJsonString(LayoutJson.Compact), new UTF8Encoding(false));
                    args.AddRange(new[] { "--params", pfile });
                    used["rptxml"] = Path.GetFileName(_rptXml);
                }
            }
            catch { /* xml hỏng: bỏ qua tham số, vẫn sinh .rpt */ }
        }

        var pdfOut = Path.Combine(WorkDir, stem + ".preview.pdf");
        if (wantPdf)
        {
            try { if (File.Exists(pdfOut)) File.Delete(pdfOut); } catch { /* đang mở */ }
            args.AddRange(new[] { "--pdf", pdfOut });
        }

        var (code, output) = await _generator.RunAsync(args.ToArray());

        // Lên đầu log: đã dùng những gì
        var head = "DAU VAO: template=" + (string?)used["template"]
                   + " | schema .xsd=" + ((string?)used["xsd"] ?? "KHONG CO")
                   + " | report .xml=" + ((string?)used["rptxml"] ?? "KHONG CO")
                   + " | quy cach=" + ((string?)used["style"] ?? "theo Excel")
                   + (_form is not null ? $" (form {_form})" : "");
        var log = head + "\n" + output;

        if (log.Contains("Failed to connect to server") || log.Contains("khoi tao duoc RAS"))
            log += "\n\nCACH KHAC PHUC NHANH: mo Crystal Reports Designer, tao 1 report rong (Blank Report), " +
                   $"luu thanh 'blank.rpt' trong thu muc:\n   {Path.GetDirectoryName(_generator.ExePath)}\n" +
                   "Tool se tu dong dung file do lam khuon, khong can RAS.";

        var okRpt = File.Exists(rptOut);
        var okPdf = wantPdf && File.Exists(pdfOut);

        // Chép sang thư mục lưu. Hỏng thì chỉ cảnh báo — file vẫn lưu tay được.
        string? saved = null;
        existing ??= ExistingOutput(outDir, outName);
        if (okRpt && existing is not null)
        {
            log += $"\n  CẢNH BÁO: đã có file \"{outName}\" trong thư mục lưu — KHÔNG ghi đè, bản xem trước " +
                   "chỉ nằm trong thư mục tạm. Muốn lưu thì đổi tên file .rpt rồi bấm \"Tạo file .rpt\".";
        }
        else if (okRpt)
        {
            try
            {
                if (outDir.Length == 0) throw new IOException("chua khai thu muc luu");
                if (!Directory.Exists(outDir)) throw new IOException($"thu muc khong ton tai: {outDir}");
                var dest = Path.Combine(outDir, outName);
                if (!string.Equals(Path.GetFullPath(dest), Path.GetFullPath(rptOut), StringComparison.OrdinalIgnoreCase))
                    File.Copy(rptOut, dest, overwrite: false);   // lỡ có ai vừa tạo cùng tên thì báo lỗi chứ không đè
                saved = dest;
                if (typedOutDir.Length > 0) _rememberOutDir(typedOutDir);
            }
            catch (Exception ex)
            {
                log += $"\n  Khong luu duoc vao thu muc: {ex.Message}\n  -> Dung nut 'Tai .rpt ve may' de luu file.";
            }
        }

        foreach (var (label, p, ok) in new[] { ("rpt", rptOut, okRpt), ("pdf", pdfOut, okPdf) })
        {
            if (!ok) continue;
            var fi = new FileInfo(p);
            log += $"\n  {label}: {fi.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes, " +
                   $"ghi luc {fi.LastWriteTime:HH:mm:ss}";
        }

        return ApiResponse.Json(200, new JsonObject
        {
            ["ok"] = okRpt,
            ["used"] = used,
            ["rpt"] = okRpt ? saved ?? rptOut : null,
            ["saved"] = saved is not null,
            ["dl"] = okRpt ? rptOut : null,
            ["name"] = outName,
            ["pdf"] = okPdf ? pdfOut : null,
            ["code"] = code,
            ["log"] = log,
        });
    }
}
