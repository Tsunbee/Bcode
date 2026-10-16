using System.Data;
using System.IO;
using System.Windows.Forms;

namespace Bcode.App.Services.Rpt;

/// <summary>
/// "Write Schema" của tab SQL Query (chế độ Web — RawSqlControl — và App — NativeSqlControl dùng chung): sinh file .xsd cho Crystal Report
/// từ các bảng kết quả của lần chạy script/procedure gần nhất. Bảng thứ i → phần tử d{i} (d1, d2, d3… đúng thứ tự result set), mỗi cột
/// một xs:element với kiểu theo kiểu .NET của cột (int → xs:int, decimal/numeric/money → xs:decimal, datetime → xs:dateTime, nvarchar → xs:string,
/// bit → xs:boolean, bigint → xs:long, float → xs:double, image/varbinary → xs:base64Binary; uniqueidentifier/sql_variant → xs:string).
/// Định dạng khớp các file mẫu FBI (vd App_Data\Controllers\Templates\Rpt\zrpt_800.xsd): chính là đầu ra của DataSet.WriteXmlSchema
/// với DataSet "NewDataSet" — UTF-8 không BOM, &lt;?xml … standalone="yes"?&gt;, msdata:IsDataSet / UseCurrentLocale, minOccurs="0".
/// </summary>
public static class XsdSchemaWriter
{
    private static string? _lastFolder;   // thư mục lưu lần trước trong phiên (dùng khi workspace không có Templates\Rpt)

    /// <summary>Chỉ chép cấu trúc (tên cột + kiểu) sang DataSet "NewDataSet" với bảng d1..dN — không chép dữ liệu và không chép
    /// ExtendedProperties (RawSqlService gắn "ms" vào từng bảng; để nguyên sẽ thành thuộc tính msprop:ms lạ trong xsd).</summary>
    public static DataSet Build(IReadOnlyList<DataTable> tables)
    {
        var ds = new DataSet("NewDataSet");
        var i = 0;
        foreach (var src in tables)
        {
            if (src.Columns.Count == 0) continue;
            var dt = new DataTable("d" + ++i);
            foreach (DataColumn c in src.Columns)
                dt.Columns.Add(c.ColumnName, XsdType(c.DataType));
            ds.Tables.Add(dt);
        }
        return ds;
    }

    // Kiểu có sẵn xs:* tương ứng (string/int/decimal/dateTime/boolean/base64Binary…) giữ nguyên. Kiểu còn lại (uniqueidentifier → Guid,
    // sql_variant → Object…) WriteXmlSchema ghi kèm msdata:DataType="…, System.Private.CoreLib, Version=8.0.0.0" — tên assembly .NET 8 mà
    // Crystal / web FBI (.NET Framework) không nạp được — nên quy về xs:dateTime (datetimeoffset) hoặc xs:string.
    private static readonly HashSet<Type> NativeXsdTypes = new()
    {
        typeof(string), typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
        typeof(long), typeof(ulong), typeof(decimal), typeof(double), typeof(float), typeof(DateTime), typeof(TimeSpan), typeof(byte[]),
    };

    private static Type XsdType(Type t) =>
        NativeXsdTypes.Contains(t) ? t : t == typeof(DateTimeOffset) ? typeof(DateTime) : typeof(string);

    /// <summary>Ghi xsd ra <paramref name="path"/>; trả về số bảng đã ghi.</summary>
    public static int Write(IReadOnlyList<DataTable> tables, string path)
    {
        using var ds = Build(tables);
        ds.WriteXmlSchema(path);   // overload theo tên file: XmlTextWriter(encoding null) → UTF-8 không BOM, thụt 2 dấu cách, CRLF — như file mẫu
        return ds.Tables.Count;
    }

    /// <summary>Luồng bấm Write Schema: chưa có bảng kết quả thì nhắc chạy script trước; có thì hỏi nơi lưu (tên gợi ý = procedure EXEC cuối
    /// trong script, thư mục gợi ý = &lt;Source Path&gt;\App_Data\Controllers\Templates\Rpt nếu có) rồi ghi, sau đó hỏi có cập nhật bảng trong file .rpt
    /// không (RptSchemaForm). Trả về dòng trạng thái, null khi huỷ / chưa có kết quả.</summary>
    public static string? SaveWithDialog(IWin32Window owner, IReadOnlyList<DataTable>? tables, string script, string? sourcePath)
    {
        if (tables is null || !tables.Any(t => t.Columns.Count > 0))
        {
            MessageBox.Show(owner, "Chưa có bảng kết quả. Chạy script / procedure (F5) trước, rồi bấm Write Schema để tạo file .xsd (d1, d2, d3… theo thứ tự bảng kết quả).",
                "Bcode — Write Schema", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        var name = ReportProfilerService.GuessName(script);
        var rptDir = string.IsNullOrWhiteSpace(sourcePath) ? null : Path.Combine(sourcePath.Trim(), "App_Data", "Controllers", "Templates", "Rpt");
        var folder = rptDir is not null && Directory.Exists(rptDir) ? rptDir
                   : _lastFolder is not null && Directory.Exists(_lastFolder) ? _lastFolder : null;

        using var sfd = new SaveFileDialog
        {
            Title = "Write Schema — lưu file .xsd cho Crystal Report",
            Filter = "XML Schema (*.xsd)|*.xsd|All files (*.*)|*.*",
            DefaultExt = "xsd",
            FileName = (name.Length > 0 ? name : "schema") + ".xsd",
        };
        if (folder is not null) sfd.InitialDirectory = folder;
        if (sfd.ShowDialog(owner) != DialogResult.OK) return null;

        try
        {
            using var ds = Build(tables);
            ds.WriteXmlSchema(sfd.FileName);
            _lastFolder = Path.GetDirectoryName(sfd.FileName);
            var count = ds.Tables.Count;
            var status = $"Đã ghi schema {count} bảng (d1{(count > 1 ? "…d" + count : "")}) ra {sfd.FileName}.";

            // Bước tiếp (tuỳ chọn): đổi nguồn bảng trong file .rpt sang xsd vừa ghi — Forms/RptSchemaForm (nạp sẵn danh sách .rpt của Templates\Rpt để tìm,
            // file .rpt cùng tên với xsd thì chọn sẵn).
            var rptName = Path.GetFileNameWithoutExtension(sfd.FileName) + ".rpt";
            var hasRpt = File.Exists(Path.ChangeExtension(sfd.FileName, ".rpt")) || (rptDir is not null && File.Exists(Path.Combine(rptDir, rptName)));
            var canPick = hasRpt || (rptDir is not null && Directory.Exists(rptDir));
            if (MessageBox.Show(owner, status + "\r\n\r\nCập nhật bảng trong file Crystal Report (.rpt) theo schema này luôn?" + (hasRpt ? $"\r\n(có sẵn {rptName})" : ""),
                    "Bcode — Write Schema", MessageBoxButtons.YesNo, MessageBoxIcon.Question, canPick ? MessageBoxDefaultButton.Button1 : MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                using var form = new Bcode.App.Forms.RptSchemaForm(sfd.FileName, ds, rptDir);
                if (form.ShowDialog(owner) == DialogResult.OK && form.ResultStatus is { } s) status += " " + s;
            }
            return status;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Bcode — Write Schema", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }
}
