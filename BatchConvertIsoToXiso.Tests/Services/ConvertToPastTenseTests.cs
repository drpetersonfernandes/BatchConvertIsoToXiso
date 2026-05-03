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
}
