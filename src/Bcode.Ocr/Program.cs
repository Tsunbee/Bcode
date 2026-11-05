using System.Text.Json;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

// Bcode.Ocr.exe ảnh.png|mẫu.pdf [--scale N] [--lang vi] [--page N] [--out-png trang.png]
// In ra JSON: { lang, scale, width, height, ms, image, pages, lines: [{ text, x, y, w, h }] } — toạ độ theo ảnh GỐC (đã chia lại tỉ lệ phóng);
// với PDF, "image" là ảnh PNG dựng từ trang (toạ độ theo ảnh đó).
// Ảnh chụp màn hình chữ nhỏ (9–10pt) đọc kém → mặc định phóng 2 lần trước khi đọc (ảnh lớn thì tự giảm để không vượt MaxImageDimension).

var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
try
{
    if (args.Length == 0) { Console.Error.WriteLine("Usage: Bcode.Ocr.exe image.png [--scale 2] [--lang vi]"); return 1; }
    var path = Path.GetFullPath(args[0]);
    double scale = 2;
    string? lang = null, outPng = null;
    var pageIndex = 0;
    var enhance = false;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--scale" && i + 1 < args.Length && double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out var s)) scale = Math.Clamp(s, 0.5, 4);
        else if (args[i] == "--lang" && i + 1 < args.Length) lang = args[++i];
        else if (args[i] == "--page" && i + 1 < args.Length && int.TryParse(args[++i], out var pg)) pageIndex = Math.Max(0, pg - 1);
        else if (args[i] == "--out-png" && i + 1 < args.Length) outPng = Path.GetFullPath(args[++i]);
        else if (args[i] == "--enhance") enhance = true;
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var file = await StorageFile.GetFileFromPathAsync(path);
    string? renderedPng = null;
    var pages = 1;
    Windows.Storage.Streams.IRandomAccessStream stream;
    if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        // PDF: dựng trang (mặc định trang 1) thành ảnh bằng bộ dựng PDF có sẵn của Windows (~200 dpi), lưu PNG để nơi gọi dò đường kẻ, rồi đọc chữ trên ảnh đó
        var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        pages = (int)pdf.PageCount;
        using var page = pdf.GetPage((uint)Math.Clamp(pageIndex, 0, Math.Max(0, pages - 1)));
        var png = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(png, new Windows.Data.Pdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Min(4000, page.Size.Width * 200 / 96),
            BitmapEncoderId = BitmapEncoder.PngEncoderId,
        });
        renderedPng = outPng ?? Path.Combine(Path.GetTempPath(), "Bcode.Ocr-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        png.Seek(0);
        using (var fs = File.Create(renderedPng)) await png.AsStreamForRead().CopyToAsync(fs);
        png.Seek(0);
        stream = png;
        scale = Math.Min(scale, 1);   // ảnh dựng từ PDF đã đủ lớn
    }
    else stream = await file.OpenAsync(FileAccessMode.Read);
    using var _ = stream;
    var decoder = await BitmapDecoder.CreateAsync(stream);
    var max = OcrEngine.MaxImageDimension;
    scale = Math.Min(scale, (double)max / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
    var transform = new BitmapTransform
    {
        ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
        ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
        InterpolationMode = BitmapInterpolationMode.Fant,
    };
    var bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
        ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
    if (enhance) bmp = Enhance(bmp);
    using var _bmp = bmp;

    // Tiếng Việt nếu máy đã cài gói OCR tiếng Việt; không thì theo ngôn ngữ máy (thường en-US — vẫn đọc được chữ, mất / sai dấu, Bcode so khớp không dấu)
    OcrEngine? engine = null;
    foreach (var tag in new[] { lang, "vi-VN", "vi" }.Where(t => !string.IsNullOrEmpty(t)))
        if (OcrEngine.IsLanguageSupported(new Language(tag!))) { engine = OcrEngine.TryCreateFromLanguage(new Language(tag!)); if (engine is not null) break; }
    engine ??= OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
    if (engine is null) { Console.Error.WriteLine("Máy chưa có ngôn ngữ OCR nào (Settings → Time & Language → Language → thêm gói ngôn ngữ)."); return 2; }

    var res = await engine.RecognizeAsync(bmp);
    var lines = res.Lines.Select(l =>
    {
        double x1 = l.Words.Min(w => w.BoundingRect.X), y1 = l.Words.Min(w => w.BoundingRect.Y);
        double x2 = l.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width), y2 = l.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
        // words: OCR hay gộp chữ của nhiều cột trên cùng đường chân chữ thành một dòng — nơi gọi cần từng từ để xếp vào đúng ô bảng
        var words = l.Words.Select(w => new { text = w.Text, x = (int)(w.BoundingRect.X / scale), y = (int)(w.BoundingRect.Y / scale),
            w = (int)(w.BoundingRect.Width / scale), h = (int)(w.BoundingRect.Height / scale) }).ToList();
        return new { text = l.Text, x = (int)(x1 / scale), y = (int)(y1 / scale), w = (int)((x2 - x1) / scale), h = (int)((y2 - y1) / scale), words };
    }).ToList();
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine(JsonSerializer.Serialize(new { lang = engine.RecognizerLanguage.LanguageTag, scale, width = decoder.PixelWidth, height = decoder.PixelHeight, ms = sw.ElapsedMilliseconds,
        image = renderedPng ?? path, pages, lines }, opts));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Lỗi OCR: " + ex.Message);
    return 3;
}

// --enhance: màn hình FBO bị lớp mờ của hộp thoại (modal) phủ — chữ phía sau chỉ đậm hơn nền ~15 mức xám nên OCR bỏ qua.
// Lấy màu nền phổ biến nhất B rồi kéo giãn độ sáng [B-40, B+8] → [0, 255] (ảnh xám): chữ mờ thành đen rõ, phần sáng (hộp thoại) thành trắng.
static SoftwareBitmap Enhance(SoftwareBitmap src)
{
    int w = src.PixelWidth, h = src.PixelHeight;
    var buf = new Windows.Storage.Streams.Buffer((uint)(w * h * 4));
    src.CopyToBuffer(buf);
    var px = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buf);
    var hist = new int[256];
    var lum = new byte[w * h];
    for (int i = 0, j = 0; j < lum.Length; i += 4, j++)
    {
        var v = (byte)((px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000);
        lum[j] = v; hist[v]++;
    }
    int bg = 1;
    for (int v = 1; v < 255; v++) if (hist[v] > hist[bg]) bg = v;
    int lo = Math.Max(0, bg - 40), hi = Math.Min(255, bg + 8);
    for (int i = 0, j = 0; j < lum.Length; i += 4, j++)
    {
        var o = (byte)Math.Clamp((lum[j] - lo) * 255 / Math.Max(1, hi - lo), 0, 255);
        px[i] = px[i + 1] = px[i + 2] = o; px[i + 3] = 255;
    }
    var outBmp = SoftwareBitmap.CreateCopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(px),
        BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
    src.Dispose();
    return outBmp;
}
