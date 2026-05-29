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
    [InlineData("copy", "copyed")]
    [InlineData("move", "moveed")]
    public void GetPastTenseReturnsExpectedPastTense(string verb, string expected)
    {
        var result = ConvertToPastTense.GetPastTense(verb);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetPastTense_EmptyString_ReturnsEd()
    {
        var result = ConvertToPastTense.GetPastTense("");
        Assert.Equal("ed", result);
    }

    [Fact]
    public void GetPastTense_UnknownVerb_AppendsEdLowercased()
    {
        var result = ConvertToPastTense.GetPastTense("UPLOAD");
        Assert.Equal("uploaded", result);
    }

    [Fact]
    public void GetPastTense_SingleCharacter_AppendsEd()
    {
        var result = ConvertToPastTense.GetPastTense("x");
        Assert.Equal("xed", result);
    }
}
