using BatchConvertIsoToXiso.Models.XisoDefinitions;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.XisoServices;

public class FileEntryTests
{
    [Fact]
    public void CreateRootEntrySetsCorrectDefaults()
    {
        var entry = FileEntry.CreateRootEntry(100);

        Assert.Equal(XisoFsFileAttributes.Directory, entry.Attributes);
        Assert.Equal(100u, entry.StartSector);
        Assert.Equal(0xFFFF, entry.LeftSubTree);
        Assert.Equal(0xFFFF, entry.RightSubTree);
        Assert.True(entry.IsDirectory);
        Assert.False(entry.HasLeftChild);
        Assert.False(entry.HasRightChild);
    }

    [Fact]
    public void IsDirectoryReturnsTrueWhenDirectoryAttributeSet()
    {
        var entry = new FileEntry
        {
            Attributes = XisoFsFileAttributes.Directory
        };
        Assert.True(entry.IsDirectory);
    }

    [Fact]
    public void IsDirectoryReturnsFalseWhenDirectoryAttributeNotSet()
    {
        var entry = new FileEntry
        {
            Attributes = XisoFsFileAttributes.ReadOnly
        };
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void HasLeftChildReturnsFalseWhenLeftSubTreeIsFfff()
    {
        var entry = new FileEntry { LeftSubTree = 0xFFFF };
        Assert.False(entry.HasLeftChild);
    }

    [Fact]
    public void HasLeftChildReturnsTrueWhenLeftSubTreeIsNotFfff()
    {
        var entry = new FileEntry { LeftSubTree = 5 };
        Assert.True(entry.HasLeftChild);
    }

    [Fact]
    public void HasRightChildReturnsFalseWhenRightSubTreeIsFfff()
    {
        var entry = new FileEntry { RightSubTree = 0xFFFF };
        Assert.False(entry.HasRightChild);
    }

    [Fact]
    public void HasRightChildReturnsTrueWhenRightSubTreeIsNotFfff()
    {
        var entry = new FileEntry { RightSubTree = 10 };
        Assert.True(entry.HasRightChild);
    }

    [Fact]
    public void ReadInternalParsesEntryCorrectly()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Write a file entry:
        // LeftSubTree: 0x0001 (ushort)
        // RightSubTree: 0xFFFF (ushort)
        // StartSector: 100 (uint)
        // FileSize: 2048 (uint)
        // Attributes: 0x01 (byte) - ReadOnly
        // NameLength: 8 (byte)
        // Name: "test.txt" (8 bytes)
        // Padding: (14 + 8) = 22, next multiple of 4 is 24, so 2 bytes padding
        writer.Write((ushort)1);
        writer.Write((ushort)0xFFFF);
        writer.Write((uint)100);
        writer.Write((uint)2048);
        writer.Write((byte)0x01);
        writer.Write((byte)8);
        writer.Write("test.txt"u8);
        writer.Write(new byte[2]); // padding
        writer.Flush();

        ms.Position = 0;
        using var reader = new BinaryReader(ms);

        var entry = new FileEntry();
        entry.ReadInternal(reader, 0, 0);

        Assert.Equal(0, entry.EntrySector);
        Assert.Equal(0, entry.EntryOffset);
        Assert.Equal(1, entry.LeftSubTree);
        Assert.Equal(0xFFFF, entry.RightSubTree);
        Assert.Equal(100u, entry.StartSector);
        Assert.Equal(2048u, entry.FileSize);
        Assert.Equal(XisoFsFileAttributes.ReadOnly, entry.Attributes);
        Assert.False(entry.IsDirectory);
        Assert.Equal("test.txt", entry.FileName);
    }

    [Fact]
    public void ReadInternalParsesDirectoryEntryCorrectly()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((ushort)0xFFFF);
        writer.Write((ushort)0xFFFF);
        writer.Write((uint)50);
        writer.Write((uint)0);
        writer.Write((byte)0x10); // Directory attribute
        writer.Write((byte)4);
        writer.Write("data"u8);
        writer.Write(new byte[2]); // padding: 14+4=18, next multiple of 4 is 20, so 2 bytes padding
        writer.Flush();

        ms.Position = 0;
        using var reader = new BinaryReader(ms);

        var entry = new FileEntry();
        entry.ReadInternal(reader, 5, 10);

        Assert.Equal(5, entry.EntrySector);
        Assert.Equal(10, entry.EntryOffset);
        Assert.Equal(0xFFFF, entry.LeftSubTree);
        Assert.Equal(0xFFFF, entry.RightSubTree);
        Assert.Equal(50u, entry.StartSector);
        Assert.True(entry.IsDirectory);
        Assert.Equal("data", entry.FileName);
    }

    [Fact]
    public void ReadInternalHandlesNullTerminatorInName()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((ushort)0xFFFF);
        writer.Write((ushort)0xFFFF);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write((byte)0x00);
        writer.Write((byte)10);
        writer.Write("file\0\0\0\0\0\0"u8); // 10 bytes with embedded null
        writer.Write(new byte[2]); // padding: 14+10=24, already aligned
        writer.Flush();

        ms.Position = 0;
        using var reader = new BinaryReader(ms);

        var entry = new FileEntry();
        entry.ReadInternal(reader, 0, 0);

        Assert.Equal("file", entry.FileName);
    }

    [Fact]
    public void ReadInternalHandlesEmptyName()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((ushort)0xFFFF);
        writer.Write((ushort)0xFFFF);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write((byte)0x00);
        writer.Write((byte)0); // name length = 0
        // No name bytes, no padding needed (14 is already aligned to 4? 14 % 4 = 2, so 2 bytes padding)
        writer.Write(new byte[2]);
        writer.Flush();

        ms.Position = 0;
        using var reader = new BinaryReader(ms);

        var entry = new FileEntry();
        entry.ReadInternal(reader, 0, 0);

        Assert.Equal(string.Empty, entry.FileName);
    }
}
