using System.Text.RegularExpressions;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public partial class ExternalToolServiceCueRegexTests
{
    // The ExternalToolService uses a generated regex. We can test the regex pattern directly
    // by replicating its behavior since it's private.

    private static readonly Regex CueRegex = MyRegex();

    [Theory]
    [InlineData("FILE \"image.bin\" BINARY", "image.bin")]
    [InlineData("FILE \"path to image.bin\" BINARY", "path to image.bin")]
    [InlineData("FILE image.bin BINARY", "image.bin")]
    [InlineData("FILE data/track01.bin BINARY", "data/track01.bin")]
    [InlineData("FILE   \"image.bin\"   BINARY  ", "image.bin")]
    public void CueRegexMatchesValidFileLines(string line, string expectedFile)
    {
        var match = CueRegex.Match(line.Trim());
        Assert.True(match.Success);
        var fileName = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
        Assert.Equal(expectedFile, fileName);
    }

    [Theory]
    [InlineData("TRACK 01 MODE2/2352")]
    [InlineData("INDEX 01 00:00:00")]
    [InlineData("REM COMMENT")]
    [InlineData("PERFORMER \"Artist\"")]
    [InlineData("TITLE \"Title\"")]
    public void CueRegexDoesNotMatchNonFileLines(string line)
    {
        var match = CueRegex.Match(line);
        Assert.False(match.Success);
    }

    [Fact]
    public void CueRegexIsCaseInsensitive()
    {
        var match = CueRegex.Match("file \"image.bin\" binary");
        Assert.True(match.Success);
    }

    [GeneratedRegex("""^FILE\s+(?:\"(.+)\"|(\S+))\s+\S+""", RegexOptions.IgnoreCase, "pt-BR")]
    private static partial Regex MyRegex();
}
