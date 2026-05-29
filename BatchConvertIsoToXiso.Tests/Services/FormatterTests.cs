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
    [InlineData(0f, 0f, "B/s")]
    [InlineData(512f, 512f, "B/s")]
    [InlineData(1023f, 1023f, "B/s")]
    [InlineData(1024f, 1f, "KB/s")]
    [InlineData(1536f, 1.5f, "KB/s")]
    [InlineData(1048576f, 1f, "MB/s")]
    [InlineData(10485760f, 10f, "MB/s")]
    public void FormatBytesPerSecondReturnsCorrectString(float bytesPerSecond, float expectedValue, string unit)
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
    public void FormatBytes_ExactBoundaryValues(long bytes, float expectedValue, string unit)
    {
        var result = Formatter.FormatBytes(bytes);
        var expected = $"{expectedValue.ToString("F1", CultureInfo.CurrentCulture)} {unit}";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatBytes_NegativeValue_ReturnsFormattedBytes()
    {
        var result = Formatter.FormatBytes(-1);
        Assert.Equal("-1 B", result);
    }

    [Theory]
    [InlineData(1024f, 1f, "KB/s")]
    [InlineData(1048576f, 1f, "MB/s")]
    public void FormatBytesPerSecond_ExactBoundaryValues(float bytesPerSecond, float expectedValue, string unit)
    {
        var result = Formatter.FormatBytesPerSecond(bytesPerSecond);
        var expected = $"{expectedValue.ToString("F1", CultureInfo.CurrentCulture)} {unit}";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatBytesPerSecond_NegativeValue_ReturnsFormattedBytes()
    {
        var result = Formatter.FormatBytesPerSecond(-1f);
        var expected = $"{(-1f).ToString("F1", CultureInfo.CurrentCulture)} B/s";
        Assert.Equal(expected, result);
    }
}
