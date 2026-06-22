using BatchConvertIsoToXiso.Services;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class ConvertToPastTenseTests
{
    [Theory]
    [InlineData("conversion", "converted")]
    [InlineData("Conversion", "converted")]
    [InlineData("CONVERSION", "converted")]
    [InlineData("test", "tested")]
    [InlineData("Test", "tested")]
    [InlineData("process", "processed")]
    [InlineData("copy", "copied")]
    [InlineData("move", "moved")]
    [InlineData("create", "created")]
    [InlineData("carry", "carried")]
    [InlineData("play", "played")]
    [InlineData("upload", "uploaded")]
    public void GetPastTenseReturnsExpectedPastTense(string verb, string expected)
    {
        var result = ConvertToPastTense.GetPastTense(verb);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetPastTenseEmptyStringReturnsEd()
    {
        var result = ConvertToPastTense.GetPastTense("");
        Assert.Equal("ed", result);
    }

    [Fact]
    public void GetPastTenseUnknownVerbAppendsEdLowercased()
    {
        var result = ConvertToPastTense.GetPastTense("DOWNLOAD");
        Assert.Equal("downloaded", result);
    }

    [Fact]
    public void GetPastTenseSingleCharacterAppendsEd()
    {
        var result = ConvertToPastTense.GetPastTense("x");
        Assert.Equal("xed", result);
    }
}
