using Bcode.App.Services.ExcelToRpt;
using Xunit;

namespace Bcode.ExcelToRpt.Tests;

/// <summary>Chuyển từ Convert\tool\test_excel_style.py — các ca lấy từ file mẫu in thật.</summary>
public class ExcelStyleTests
{
    [Theory]
    [InlineData(@"_(* #,##0_);_(* \(#,##0\);_(@_)", "#,##0", false)]
    [InlineData(@"_(* #,##0.000_);_(* \(#,##0.000\);_(* """"_);_(@_)", "#,##0.000", false)]
    [InlineData(@"_(* #,##0.0000_);_(* \(#,##0.0000\);_(* ""-""??_);_(@_)", "#,##0.0000", false)]
    [InlineData(@"_-* #,##0_-;\-* #,##0_-;_-* "" - ""_-;_-@_-", "#,##0", false)]
    [InlineData(@"###\ ###\ ###\ ###\ ##0", "#,##0", false)]
    [InlineData(@"###\ ###\ ###", "#,##0", false)]
    [InlineData("mm-dd-yy", "MM-dd-yy", false)]
    [InlineData("dd/mm/yyyy", "dd/MM/yyyy", false)]
    [InlineData("d/m/yy", "d/M/yy", false)]
    [InlineData("[$-409]dd-mmm-yy;@", "dd-MMM-yy", false)]
    [InlineData("General", null, false)]
    [InlineData("@", null, false)]
    [InlineData(";;;", null, true)]
    [InlineData("0.00", "0.00", false)]
    [InlineData("0%", "0", false)]
    [InlineData("#,##0.00;[Red]-#,##0.00", "#,##0.00", false)]
    [InlineData("h:mm:ss", null, false)]
    [InlineData("dd/mm/yyyy hh:mm", "dd/MM/yyyy", false)]
    public void Number_format(string excel, string? want, bool hidden)
    {
        var (got, hid) = ExcelStyle.CrystalNumberFormat(excel);
        Assert.Equal(want, got);
        Assert.Equal(hidden, hid);
    }

    [Theory]
    [InlineData("FFFFFF", -0.35, "A6A6A6")]
    [InlineData("FFFFFF", -0.15, "D9D9D9")]
    [InlineData("000000", 0.35, "595959")]
    [InlineData("FFFFFF", 0, "FFFFFF")]
    public void Tint(string hex, double tint, string want) =>
        Assert.Equal(want, ExcelStyle.ApplyTint(hex, tint));

    [Theory]
    [InlineData(1234.0, "1234.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(1e16, "1e+16")]
    [InlineData(123456789.125, "123456789.125")]
    [InlineData(-2.5, "-2.5")]
    public void Python_float_repr(double v, string want) => Assert.Equal(want, PyFormat.Float(v));

    [Theory]
    [InlineData(9.35, 9.3)]       // 9.35 nhị phân là 9.3499999... -> Python ra 9.3
    [InlineData(11.0, 11.0)]
    [InlineData(10.25, 10.2)]     // đúng nửa -> làm tròn về chẵn
    public void Python_round_1(double v, double want) => Assert.Equal(want, ExcelLayoutParser.PyRound1(v));
}
