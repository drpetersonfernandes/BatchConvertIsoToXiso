using BatchConvertIsoToXiso.Services;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class GetApplicationVersionTests
{
    [Fact]
    public void GetProgramVersionReturnsNonNullString()
    {
        var version = GetApplicationVersion.GetProgramVersion();
        Assert.False(string.IsNullOrEmpty(version));
    }

    [Fact]
    public void GetProgramVersionReturnsVersionFormat()
    {
        var version = GetApplicationVersion.GetProgramVersion();
        // Should be in format x.x.x.x or similar
        Assert.Contains('.', version);
    }
}
