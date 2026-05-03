using BatchConvertIsoToXiso.Services;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class CheckForTempPathTests
{
    [Fact]
    public void IsSystemTempPathWithSystemTempPathReturnsTrue()
    {
        var tempPath = Path.GetTempPath();
        Assert.True(CheckForTempPath.IsSystemTempPath(tempPath));
    }

    [Fact]
    public void IsSystemTempPathWithSubfolderOfTempPathReturnsTrue()
    {
        var tempPath = Path.GetTempPath();
        var subfolder = Path.Combine(tempPath, "BatchConvertIsoToXiso_Test");
        Assert.True(CheckForTempPath.IsSystemTempPath(subfolder));
    }

    [Fact]
    public void IsSystemTempPathWithNonTempPathReturnsFalse()
    {
        var path = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? throw new InvalidOperationException(), "SomeRandomFolder");
        Assert.False(CheckForTempPath.IsSystemTempPath(path));
    }

    [Fact]
    public void IsSystemTempPathWithCurrentDirectoryReturnsFalse()
    {
        Assert.False(CheckForTempPath.IsSystemTempPath(Environment.CurrentDirectory));
    }
}
