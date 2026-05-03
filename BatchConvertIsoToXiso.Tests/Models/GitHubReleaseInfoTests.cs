using BatchConvertIsoToXiso.Models;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Models;

public class GitHubReleaseInfoTests
{
    [Fact]
    public void DefaultValuesAreNull()
    {
        var info = new GitHubReleaseInfo();

        Assert.Null(info.TagName);
        Assert.Null(info.HtmlUrl);
    }

    [Fact]
    public void PropertiesCanBeSet()
    {
        var info = new GitHubReleaseInfo
        {
            TagName = "v2.3.1",
            HtmlUrl = "https://github.com/test/releases/tag/v2.3.1"
        };

        Assert.Equal("v2.3.1", info.TagName);
        Assert.Equal("https://github.com/test/releases/tag/v2.3.1", info.HtmlUrl);
    }
}
