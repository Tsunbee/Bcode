namespace Bcode.App.UI;

/// <summary>
/// Binds a DataGridView's data source the way a result grid should feel responsive doing it:
/// one autosize pass right after loading, then fixed column widths — instead of leaving
/// AutoSizeColumnsMode continuously on, which recalculates every column's width on basically
/// every scroll/paint and is what makes a wide result grid with a few hundred+ rows feel
/// stiff ("đơ") while scrolling. Also turns on double buffering (off by default on
/// DataGridView) to cut the flicker/tearing that shows up scrolling a large grid without it.
/// </summary>
public static class GridDisplayHelper
{
    public static void BindOptimized(DataGridView grid, object? dataSource)
    {
        EnableDoubleBuffering(grid);
        EnableRowNumbers(grid);

        grid.AllowUserToOrderColumns = true; // kéo thả đổi vị trí cột ngay trên tiêu đề
        grid.SuspendLayout();
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        // Bỏ hẳn cột cũ trước khi gán nguồn mới: nếu không, DataGridView giữ lại các cột trùng tên của lần tải trước và chỉ thêm
        // cột mới vào cuối nên thứ tự cột của câu SELECT mới (vd "s4, *") không hiện ra đúng.
        grid.DataSource = null;
        grid.Columns.Clear();
        grid.DataSource = dataSource;
        // Before the sizing pass below — it formats cells, and that is where an image column
        // over non-image bytes throws.
        ReplaceBinaryImageColumns(grid);
        // One-time sizing pass based on what's actually in the columns — same visual result
        // as leaving AutoSizeColumnsMode on DisplayedCells permanently, minus the ongoing
        // per-scroll recalculation cost.
        if (grid.Columns.Count > 0)
        {
            grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
            CapColumnWidths(grid);
        }

        // Widen the row-header column to fit the actual row count (e.g. "1234" needs more
        // room than "1") now that the data is bound and Rows.Count is final.
        var digits = Math.Max(2, grid.Rows.Count.ToString().Length);
        grid.RowHeadersWidth = Math.Max(40, TextRenderer.MeasureText(new string('9', digits), grid.Font).Width + 24);

        grid.ResumeLayout();
    }

    /// <summary>Bề rộng tối đa khi tự giãn cột theo dữ liệu (px ở 96 DPI) — cột chứa chuỗi rất dài (vd ds_vt, ghi chú, công thức)
    /// không kéo cả lưới ra; phần dư hiện "…", rê chuột vào ô thấy đủ nội dung, cần rộng hơn thì kéo mép cột.</summary>
    public const int MaxAutoColumnWidth = 300;

    private static void CapColumnWidths(DataGridView grid)
    {
        var max = (int)Math.Round(MaxAutoColumnWidth * grid.DeviceDpi / 96.0);
        grid.ShowCellToolTips = true;   // ô bị cắt: rê chuột hiện đủ giá trị
        foreach (DataGridViewColumn col in grid.Columns)
        {
            if (col.Width <= max) continue;
            col.Width = max;
            col.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        }
    }

    /// <summary>DataGridView's row-header column exists by default but is blank — this draws
    /// a 1-based row number into it, the "phần kết quả cũng có số dòng" a plain result grid
    /// is otherwise missing (unlike the script box's own new line-number gutter). Wired once
    /// per grid (RowPostPaint -= then += with the same static method group is idempotent, so
    /// re-binding the same grid instance repeatedly across runs doesn't stack handlers).</summary>
    private static void EnableRowNumbers(DataGridView grid)
    {
        grid.RowHeadersVisible = true;
        grid.RowPostPaint -= DrawRowNumber;
        grid.RowPostPaint += DrawRowNumber;
    }

    private static void DrawRowNumber(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        if (sender is not DataGridView grid) return;
        var bounds = new Rectangle(e.RowBounds.Left, e.RowBounds.Top, grid.RowHeadersWidth, e.RowBounds.Height);
        TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(), grid.Font, bounds,
            grid.RowHeadersDefaultCellStyle.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }

    private const string BinaryColumnTag = "bcode-binary";
    private const int BinaryPreviewBytes = 32;

    /// <summary>
    /// DataGridView auto-generates a <see cref="DataGridViewImageColumn"/> for every byte[]
    /// column (SQL image / varbinary) and tries to decode each value as a picture. Columns that
    /// hold arbitrary files — filelib.file_data and the like — are not pictures, so every cell
    /// raised "Parameter is not valid" in the DataGridView Default Error Dialog. Such columns are
    /// swapped for a read-only text column showing the bytes as hex, the way SSMS does
    /// ("0x4D5A90… (12,345 bytes)"). Read-only because the text is a preview, not something
    /// that could be parsed back into the value on save.
    /// </summary>
    private static void ReplaceBinaryImageColumns(DataGridView grid)
    {
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            if (grid.Columns[i] is not DataGridViewImageColumn image || image.ValueType != typeof(byte[])) continue;
            var text = new DataGridViewTextBoxColumn
            {
                Name = image.Name,
                HeaderText = image.HeaderText,
                DataPropertyName = image.DataPropertyName,
                ValueType = typeof(byte[]),
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                Tag = BinaryColumnTag,
            };
            var displayIndex = image.DisplayIndex;
            grid.Columns.RemoveAt(i);
            grid.Columns.Insert(i, text);
            text.DisplayIndex = displayIndex;
        }
        grid.CellFormatting -= FormatBinaryCell;
        grid.CellFormatting += FormatBinaryCell;
        grid.CellPainting -= PaintImageCell;
        grid.CellPainting += PaintImageCell;
        grid.CellDoubleClick -= OpenImageCell;
        grid.CellDoubleClick += OpenImageCell;
        grid.Sorted -= OnSortedApplyImageRows;
        grid.Sorted += OnSortedApplyImageRows;
        ApplyImageRowHeights(grid);
    }

    // ---- Binary cells that really are pictures: thumbnail in the cell, full size on double-click ----

    private const int ImageRowHeight = 64;
    private const int ThumbMaxWidth = 120;

    private sealed record ImageInfo(Bitmap Thumb, string Caption);

    /// <summary>Decoded thumbnails, keyed on the byte[] itself so they live exactly as long as
    /// the row data does; null = "looked, not a picture". Decoding happens once per value,
    /// not once per paint.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], ImageInfo?> ImageCache = new();

    /// <summary>Formats GDI+ can decode, recognised by their first bytes — checked before
    /// decoding so a PDF/XLSX/RPT never goes near Image.FromStream (that is what threw).</summary>
    private static bool LooksLikeImage(byte[] b) =>
        b.Length > 8 && (
            (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||            // PNG
            (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||                             // JPEG
            (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) ||            // GIF
            (b[0] == 0x42 && b[1] == 0x4D) ||                                             // BMP
            (b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00) ||            // ICO
            (b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A && b[3] == 0x00) ||            // TIFF (LE)
            (b[0] == 0x4D && b[1] == 0x4D && b[2] == 0x00 && b[3] == 0x2A));             // TIFF (BE)

    private static ImageInfo? GetImageInfo(byte[] bytes)
    {
        if (!LooksLikeImage(bytes)) return null;
        return ImageCache.GetValue(bytes, static b =>
        {
            try
            {
                using var ms = new MemoryStream(b);
                using var img = Image.FromStream(ms);
                var scale = Math.Min(1.0, Math.Min((double)ThumbMaxWidth / img.Width, (double)(ImageRowHeight - 6) / img.Height));
                var thumb = new Bitmap(img, Math.Max(1, (int)(img.Width * scale)), Math.Max(1, (int)(img.Height * scale)));
                return new ImageInfo(thumb, $"{img.Width}×{img.Height} · {FormatSize(b.Length)}");
            }
            catch
            {
                return null; // right magic bytes, broken body — falls back to hex text
            }
        });
    }

    private static string FormatSize(long n) =>
        n >= 1024 * 1024 ? $"{n / 1024.0 / 1024.0:0.#} MB" : n >= 1024 ? $"{n / 1024.0:0.#} KB" : $"{n} B";

    private static bool IsBinaryColumn(DataGridView grid, int col) =>
        col >= 0 && col < grid.Columns.Count && Equals(grid.Columns[col].Tag, BinaryColumnTag);

    /// <summary>Taller rows only where a picture is shown — the rest of the grid keeps its
    /// normal density. Re-run after a sort, since rows are reordered.</summary>
    private static void ApplyImageRowHeights(DataGridView grid)
    {
        var cols = grid.Columns.Cast<DataGridViewColumn>().Where(c => Equals(c.Tag, BinaryColumnTag)).Select(c => c.Index).ToList();
        if (cols.Count == 0) return;
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow) continue;
            if (cols.Any(c => row.Cells[c].Value is byte[] b && LooksLikeImage(b)) && row.Height < ImageRowHeight)
                row.Height = ImageRowHeight;
        }
    }

    private static void OnSortedApplyImageRows(object? sender, EventArgs e)
    {
        if (sender is DataGridView grid) ApplyImageRowHeights(grid);
    }

    private static void PaintImageCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0 || !IsBinaryColumn(grid, e.ColumnIndex)) return;
        if (e.Value is not byte[] bytes || GetImageInfo(bytes) is not { } info || e.Graphics is null) return;

        e.PaintBackground(e.CellBounds, true);
        var b = e.CellBounds;
        var thumbRect = new Rectangle(b.Left + 4, b.Top + (b.Height - info.Thumb.Height) / 2, info.Thumb.Width, info.Thumb.Height);
        e.Graphics.DrawImage(info.Thumb, thumbRect);
        var textRect = new Rectangle(thumbRect.Right + 8, b.Top, Math.Max(0, b.Right - thumbRect.Right - 12), b.Height);
        TextRenderer.DrawText(e.Graphics, info.Caption, e.CellStyle?.Font ?? grid.Font, textRect,
            AppColors.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        e.Handled = true;
    }

    /// <summary>Double-click a picture cell → the full-size image in a small window.</summary>
    private static void OpenImageCell(object? sender, DataGridViewCellEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0 || !IsBinaryColumn(grid, e.ColumnIndex)) return;
        if (grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value is not byte[] bytes || GetImageInfo(bytes) is not { } info) return;

        Image full;
        try { using var ms = new MemoryStream(bytes); using var img = Image.FromStream(ms); full = new Bitmap(img); }
        catch { return; }

        var screen = Screen.FromControl(grid).WorkingArea;
        var form = new Form
        {
            Text = $"{grid.Columns[e.ColumnIndex].HeaderText} — {info.Caption}",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(Math.Clamp(full.Width, 240, screen.Width * 3 / 4), Math.Clamp(full.Height, 160, screen.Height * 3 / 4)),
            KeyPreview = true,
            ShowInTaskbar = false,
            BackColor = AppColors.Background,
        };
        var picture = new PictureBox { Dock = DockStyle.Fill, Image = full, SizeMode = PictureBoxSizeMode.Zoom, BackColor = AppColors.Background };
        form.Controls.Add(picture);
        form.KeyDown += (_, k) => { if (k.KeyCode == Keys.Escape) form.Close(); };
        form.FormClosed += (_, _) => { picture.Image = null; full.Dispose(); form.Dispose(); };
        form.Show(grid.FindForm());
    }

    private static void FormatBinaryCell(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (sender is not DataGridView grid || e.ColumnIndex < 0 || e.Value is not byte[] bytes) return;
        if (!Equals(grid.Columns[e.ColumnIndex].Tag, BinaryColumnTag)) return;
        var hex = Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, BinaryPreviewBytes));
        e.Value = bytes.Length > BinaryPreviewBytes
            ? $"0x{hex}… ({bytes.Length:N0} bytes)"
            : "0x" + hex;
        e.FormattingApplied = true;
    }

    /// <summary>DataGridView (like most WinForms controls) has DoubleBuffered as a protected
    /// property — reflection is the standard way to flip it on from outside the control's
    /// own class without subclassing just for this. Idempotent, so it's fine to call on
    /// every bind rather than tracking whether it's already been set.</summary>
    private static void EnableDoubleBuffering(DataGridView grid) =>
        typeof(DataGridView)
            .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(grid, true);
}
