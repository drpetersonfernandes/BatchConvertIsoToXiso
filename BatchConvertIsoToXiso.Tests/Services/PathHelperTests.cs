using BatchConvertIsoToXiso.Services;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class PathHelperTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(@"\\server\share", null)]
    [InlineData(@"C:\test\file.iso", "C:")]
    [InlineData("C:/test/file.iso", "C:")]
    [InlineData(@"D:\", "D:")]
    public void GetDriveLetterReturnsExpectedResult(string? path, string? expected)
    {
        var result = PathHelper.GetDriveLetter(path);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(@"C:\test", false)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\folder", true)]
    public void IsUncPathReturnsExpectedResult(string? path, bool expected)
    {
        var result = PathHelper.IsUncPath(path);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"C:\test", false)]
    public void IsNetworkPathWithUncPathReturnsTrue(string? path, bool expected)
    {
        // Note: mapped network drives cannot be tested without actual network drives
        var result = PathHelper.IsNetworkPath(path);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData(@"C:\test", null, null)]
    [InlineData(@"\\server\share", "server", "share")]
    [InlineData(@"\\server\share\folder\file.iso", "server", "share")]
    public void TryGetUncShareInfoReturnsExpectedResult(string? path, string? expectedServer, string? expectedShare)
    {
        var result = PathHelper.TryGetUncShareInfo(path);

        if (expectedServer == null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(expectedServer, result.Value.Server);
            Assert.Equal(expectedShare, result.Value.Share);
        }
    }

    [Fact]
    public void IsNetworkErrorWithNullExceptionReturnsFalse()
    {
        Assert.False(PathHelper.IsNetworkError(null));
    }

    [Theory]
    [InlineData("network path was not found", true)]
    [InlineData("network name is no longer available", true)]
    [InlineData("an unexpected network error occurred", true)]
    [InlineData("the semaphore timeout period has expired", true)]
    [InlineData("the network connection was aborted", true)]
    [InlineData("the network connection was reset", true)]
    [InlineData("some random error message", false)]
    [InlineData("file not found", false)]
    [InlineData("access denied", false)]
    public void IsNetworkErrorWithKnownPatternsReturnsExpectedResult(string message, bool expected)
    {
        var ex = new IOException(message);
        Assert.Equal(expected, PathHelper.IsNetworkError(ex));
    }

    [Fact]
    public void IsNetworkErrorCaseInsensitive()
    {
        var ex = new IOException("NETWORK PATH WAS NOT FOUND");
        Assert.True(PathHelper.IsNetworkError(ex));
    }
}
