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
    public void GetXgdTypeReturnsExpectedMapping(int redumpType, int expectedXgd)
    {
        var result = XisoWriter.GetXgdType(redumpType);
        Assert.Equal(expectedXgd, result);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(8, 0)]
    [InlineData(100, 0)]
    public void GetXgdTypeOutOfRangeReturnsZero(int redumpType, int expectedXgd)
    {
        var result = XisoWriter.GetXgdType(redumpType);
        Assert.Equal(expectedXgd, result);
    }
}
