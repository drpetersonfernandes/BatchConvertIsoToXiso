using BatchConvertIsoToXiso.Services.XisoServices;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.XisoServices;

public class XisoWriterTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(6, 3)]
    [InlineData(7, 3)]
    [InlineData(-1, 0)]
    [InlineData(8, 0)]
    [InlineData(99, 0)]
    public void GetXgdType_ReturnsCorrectXgdType(int redumpIsoType, int expectedXgdType)
    {
        var result = XisoWriter.GetXgdType(redumpIsoType);
        Assert.Equal(expectedXgdType, result);
    }
}
