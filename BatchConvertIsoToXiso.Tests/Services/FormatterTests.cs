using System.Globalization;
using BatchConvertIsoToXiso.Services;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class FormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    public void FormatBytesSmallValuesReturnsBytes(long bytes, string expected)
    {
        var result = Formatter.FormatBytes(bytes);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1024, 1, "KB")]
    [InlineData(1536, 1, "KB")] // integer division: 1536/1024 = 1
    [InlineData(1048576, 1, "MB")]
    [InlineData(1073741824, 1, "GB")]
    [InlineData(1099511627776, 1, "TB")]
    [InlineData(2199023255552, 2, "TB")]
    public void FormatBytesLargeValuesReturnsFormattedWithUnit(long bytes, long expectedValue, string unit)
    {
        var result = Formatter.FormatBytes(bytes);
        var expected = $"{expectedValue.ToString("F1", CultureInfo.CurrentCulture)} {unit}";
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0d, 0d, "B/s")]
    [InlineData(512d, 512d, "B/s")]
    [InlineData(1023d, 1023d, "B/s")]
    [InlineData(1024d, 1d, "KB/s")]
    [InlineData(1536d, 1.5d, "KB/s")]
    [InlineData(1048576d, 1d, "MB/s")]
    [InlineData(10485760d, 10d, "MB/s")]
    public void FormatBytesPerSecondReturnsCorrectString(double bytesPerSecond, double expectedValue, string unit)
    {
        var result = Formatter.FormatBytesPerSecond(bytesPerSecond);
        var expected = $"{expectedValue.ToString("F1", CultureInfo.CurrentCulture)} {unit}";
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1024, 1f, "KB")]
    [InlineData(1048576, 1f, "MB")]
    [InlineData(1073741824, 1f, "GB")]
    [InlineData(1099511627776, 1f, "TB")]
    public void FormatBytesExactBoundaryValues(long bytes, float expectedValue, string unit)
    {
        var result = Formatter.FormatBytes(bytes);
        var expected = $"{expectedValue.ToString("F1", CultureInfo.CurrentCulture)} {unit}";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatBytesNegativeValueReturnsFormattedBytes()
    {
        var result = Formatter.FormatBytes(-1);
        Assert.Equal("-1 B", result);
    }

    [Theory]
    [InlineData(1024d, 1d, "KB/s")]
    [InlineData(1048576d, 1d, "MB/s")]
    public void FormatBytesPerSecondExactBoundaryValues(double bytesPerSecond, double expectedValue, string unit)
    {
        var result = Formatter.FormatBytesPerSecond(bytesPerSecond);
        var expected = $"{expectedValue.ToString("F1", CultureInfo.CurrentCulture)} {unit}";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatBytesPerSecondNegativeValueReturnsFormattedBytes()
    {
        var result = Formatter.FormatBytesPerSecond(-1d);
        var expected = $"{(-1d).ToString("F1", CultureInfo.CurrentCulture)} B/s";
        Assert.Equal(expected, result);
    }
}
