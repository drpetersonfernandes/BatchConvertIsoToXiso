using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Models;

public class XisoExplorerItemTests
{
    [Fact]
    public void DefaultValuesAreSetCorrectly()
    {
        var item = new XisoExplorerItem();

        Assert.Equal(string.Empty, item.Name);
        Assert.Equal(string.Empty, item.SizeFormatted);
        Assert.False(item.IsDirectory);
        Assert.Equal("File", item.Type);
    }

    [Fact]
    public void TypeReturnsFolderWhenIsDirectoryIsTrue()
    {
        var item = new XisoExplorerItem { IsDirectory = true };
        Assert.Equal("Folder", item.Type);
    }

    [Fact]
    public void TypeReturnsFileWhenIsDirectoryIsFalse()
    {
        var item = new XisoExplorerItem { IsDirectory = false };
        Assert.Equal("File", item.Type);
    }

    [Fact]
    public void PropertiesCanBeInitialized()
    {
        var entry = FileEntry.CreateRootEntry(0);
        var item = new XisoExplorerItem
        {
            Name = "default.xbe",
            SizeFormatted = "1.5 MB",
            IsDirectory = false,
            Entry = entry
        };

        Assert.Equal("default.xbe", item.Name);
        Assert.Equal("1.5 MB", item.SizeFormatted);
        Assert.False(item.IsDirectory);
        Assert.Same(entry, item.Entry);
    }
}
