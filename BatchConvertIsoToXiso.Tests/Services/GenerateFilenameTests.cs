using BatchConvertIsoToXiso.Services;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class GenerateFilenameTests
{
    [Theory]
    [InlineData(0, "iso_000000.iso")]
    [InlineData(1, "iso_000001.iso")]
    [InlineData(999, "iso_000999.iso")]
    [InlineData(1000, "iso_001000.iso")]
    [InlineData(999999, "iso_999999.iso")]
    public void GenerateSimpleFilenameReturnsExpectedFilename(int index, string expected)
    {
        var result = GenerateFilename.GenerateSimpleFilename(index);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GenerateSimpleFilename_NegativeIndex_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GenerateFilename.GenerateSimpleFilename(-1));
    }

    [Fact]
    public void GenerateSimpleFilename_OverflowIndex_FormatsBeyondSixDigits()
    {
        var result = GenerateFilename.GenerateSimpleFilename(1000000);
        Assert.Equal("iso_1000000.iso", result);
    }

    [Fact]
    public void GenerateSimpleFilename_EndsWithIsoExtension()
    {
        var result = GenerateFilename.GenerateSimpleFilename(42);
        Assert.EndsWith(".iso", result);
    }

    [Fact]
    public void GenerateSimpleFilename_StartsWithIsoPrefix()
    {
        var result = GenerateFilename.GenerateSimpleFilename(42);
        Assert.StartsWith("iso_", result);
    }
}
