using System.Globalization;
using System.Numerics;

namespace Bcode.App.Services.ExcelToRpt;

/// <summary>
/// Tái hiện <c>str()</c> của Python cho các giá trị đọc từ ô Excel.
///
/// Chữ trong ô (kể cả số) đi thẳng vào layout.json — mẫu chưa đánh dấu thì CHÍNH những con
/// số này là nội dung in ra. Python in 1234.0 là "1234.0", 1e-05 là "1e-05", ngày là
/// "2024-01-31 00:00:00"; .NET in khác ở cả ba, nên phải định dạng lại cho khớp bản Python.
/// </summary>
internal static class PyFormat
{
    private static readonly DateTime WindowsEpoch = new(1899, 12, 30);
    private static readonly DateTime MacEpoch = new(1904, 1, 1);

    public static string Int(string digits) =>
        BigInteger.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var bi)
            ? bi.ToString(CultureInfo.InvariantCulture) : digits;

    /// <summary>repr(float) của Python: ngắn nhất mà vẫn đọc lại đúng, dạng mũ khi mũ &lt; -4 hoặc ≥ 16.</summary>
    public static string Float(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";

        // "R" của .NET Core 3+ cho đúng dãy chữ số ngắn nhất; chỉ cần bóc ra rồi dựng lại
        var shortest = d.ToString("R", CultureInfo.InvariantCulture);
        var neg = shortest.StartsWith('-');
        if (neg) shortest = shortest[1..];

        string mant; int exp;
        var ei = shortest.IndexOfAny(new[] { 'E', 'e' });
        if (ei >= 0)
        {
            mant = shortest[..ei];
            exp = int.Parse(shortest[(ei + 1)..], CultureInfo.InvariantCulture);
        }
        else { mant = shortest; exp = 0; }

        var dot = mant.IndexOf('.');
        var intPart = dot >= 0 ? mant[..dot] : mant;
        var frac = dot >= 0 ? mant[(dot + 1)..] : "";
        var digits = (intPart + frac).TrimStart('0');
        // vị trí dấu chấm thập phân tính từ đầu 'digits'
        var lead = (intPart + frac).Length - (intPart + frac).TrimStart('0').Length;
        var pointPos = intPart.Length + exp - lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        var sciExp = pointPos - 1;
        string body;
        if (sciExp < -4 || sciExp >= 16)
        {
            var m = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
            body = m + "e" + (sciExp < 0 ? "-" : "+") + Math.Abs(sciExp).ToString("00", CultureInfo.InvariantCulture);
        }
        else if (pointPos <= 0)
            body = "0." + new string('0', -pointPos) + digits;
        else if (pointPos >= digits.Length)
            body = digits + new string('0', pointPos - digits.Length) + ".0";
        else
            body = digits[..pointPos] + "." + digits[pointPos..];
        return (neg ? "-" : "") + body;
    }

    public static string DateTime(DateTime d)
    {
        var s = d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var micro = (int)(d.Ticks % TimeSpan.TicksPerSecond / 10);
        return micro != 0 ? s + "." + micro.ToString("000000", CultureInfo.InvariantCulture) : s;
    }

    private static string Time(TimeSpan t)
    {
        var s = $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
        var micro = (int)(t.Ticks % TimeSpan.TicksPerSecond / 10);
        return micro != 0 ? s + "." + micro.ToString("000000", CultureInfo.InvariantCulture) : s;
    }

    /// <summary>openpyxl.utils.datetime.from_excel, trả về str() của kết quả; null = ngoài giới hạn.</summary>
    public static PyText? FromExcel(double value, bool date1904, bool timedelta)
    {
        try
        {
            if (timedelta)
            {
                var td = TimeSpan.FromDays(value);
                var days = (int)Math.Floor(td.TotalDays);
                var rest = td - TimeSpan.FromDays(days);
                var head = days == 0 ? "" : days + (Math.Abs(days) == 1 ? " day, " : " days, ");
                return new PyText(head + $"{(int)rest.TotalHours}:{rest.Minutes:00}:{rest.Seconds:00}");
            }

            var day = Math.Floor(value);
            var fraction = value - day;
            var diff = TimeSpan.FromMilliseconds(Math.Round(fraction * 86400 * 1000));
            if (value >= 0 && value < 1 && diff.Days == 0)
                return new PyText(Time(diff));
            if (value > 0 && value < 60 && !date1904) day += 1;
            var epoch = date1904 ? MacEpoch : WindowsEpoch;
            return new PyText(DateTime(epoch.AddDays(day) + diff));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>str() của một giá trị ô bất kỳ.</summary>
    public static string Str(object? v) => v switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        _ => v.ToString() ?? "",
    };

    /// <summary>str.strip() của Python (khoảng trắng Unicode).</summary>
    public static string Strip(string s) => s.Trim();
}
