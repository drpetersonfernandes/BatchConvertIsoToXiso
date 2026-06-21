using System.Globalization;
using BatchConvertIsoToXiso.Services;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class FolderIconConverterTests
{
    private readonly FolderIconConverter _converter = new();

    [Fact]
    public void ConvertWithTrueReturnsFolderEmoji()
    {
        var result = _converter.Convert(true, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("📁", result);
    }

    [Fact]
    public void ConvertWithFalseReturnsFileEmoji()
    {
        var result = _converter.Convert(false, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("📄", result);
    }

    [Fact]
    public void ConvertWithNullReturnsFileEmoji()
    {
        var result = _converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("📄", result);
    }

    [Fact]
    public void ConvertBackThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() =>
            _converter.ConvertBack("📁", typeof(bool), null, CultureInfo.InvariantCulture));
    }
}
