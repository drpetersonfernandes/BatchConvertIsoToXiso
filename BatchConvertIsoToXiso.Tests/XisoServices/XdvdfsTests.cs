using BatchConvertIsoToXiso.Services.XisoServices.XDVDFS;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.XisoServices;

public class XdvdfsTests
{
    private static readonly byte[] MagicId = "MICROSOFT*XBOX*MEDIA"u8.ToArray();

    private static string CreateMinimalXisoFile(long gamePartitionOffset = 0)
    {
        var path = Path.GetTempFileName();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var headerOffset = gamePartitionOffset + 0x10000;
        const int sectorSize = 2048;

        // Root directory at sector 34 (well after header sector 32)
        const int rootDirSector = 34;
        var rootDirOffset = gamePartitionOffset + (long)rootDirSector * sectorSize;
        var requiredSize = rootDirOffset + 64;
        fs.SetLength(requiredSize);

        // Write header at sector 32
        fs.Position = headerOffset;
        fs.Write(MagicId); // 20 bytes
        fs.Write(BitConverter.GetBytes(rootDirSector)); // root offset (4 bytes)
        fs.Write(BitConverter.GetBytes(64u)); // root size (4 bytes)

        // Write secondary magic at +0x7EC
        fs.Position = headerOffset + 0x7EC;
        fs.Write(MagicId);

        // Write root directory entry at sector 34
        fs.Position = rootDirOffset;
        fs.Write(BitConverter.GetBytes((ushort)0xFFFF)); // left child
        fs.Write(BitConverter.GetBytes((ushort)0xFFFF)); // right child
        fs.Write(BitConverter.GetBytes(0u)); // start sector
        fs.Write(BitConverter.GetBytes(0u)); // file size
        fs.Write([(byte)0x10]); // attributes = directory
        fs.Write([(byte)1]); // name length
        fs.Write([(byte)'x']); // name = "x"
        // padding: 14 + 1 = 15, need 1 byte padding
        fs.Write([(byte)0]);

        return path;
    }

    [Fact]
    public void FindXisoSignatureOffsetWithValidXisoAtZeroReturnsZero()
    {
        var path = CreateMinimalXisoFile();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
        var result = Xdvdfs.FindXisoSignatureOffset(fs);
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindXisoSignatureOffsetWithValidXisoAtXgd3OffsetReturnsOffset()
    {
        const int offset = 34078720; // XGD3 offset
        var path = CreateMinimalXisoFile(offset);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
        var result = Xdvdfs.FindXisoSignatureOffset(fs);
        Assert.Equal(offset, result);
    }

    [Fact]
    public void FindXisoSignatureOffsetWithInvalidIsoReturnsNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[1024 * 1024]);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);

        var result = Xdvdfs.FindXisoSignatureOffset(fs);
        Assert.Null(result);
    }

    [Fact]
    public void ValidateXisoSignatureAtOffsetWithValidSignatureReturnsTrue()
    {
        var path = CreateMinimalXisoFile();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
        var result = Xdvdfs.ValidateXisoSignatureAtOffset(fs, 0);
        Assert.True(result);
    }

    [Fact]
    public void ValidateXisoSignatureAtOffsetWithInvalidSignatureReturnsFalse()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[1024 * 1024]);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);

        var result = Xdvdfs.ValidateXisoSignatureAtOffset(fs, 0);
        Assert.False(result);
    }

    [Fact]
    public void GetXisoRangesWithValidXisoReturnsRanges()
    {
        var path = CreateMinimalXisoFile();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
        var ranges = Xdvdfs.GetXisoRanges(fs, 0, true, false);

        Assert.NotEmpty(ranges);
        // Should contain header sectors and root directory sectors
    }

    [Fact]
    public void GetXisoRangesWithInvalidSignatureThrowsInvalidDataException()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[1024 * 1024]);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);

        Assert.Throws<InvalidDataException>(() => Xdvdfs.GetXisoRanges(fs, 0, true, false));
    }

    [Fact]
    public void GetXisoRangesWithEmptyFilesystemReturnsEmptyRanges()
    {
        var path = Path.GetTempFileName();
        using (var buildFs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            const int headerOffset = 0x10000;
            buildFs.SetLength(headerOffset + 0x800);

            buildFs.Position = headerOffset;
            buildFs.Write(MagicId);
            buildFs.Write(BitConverter.GetBytes(0u)); // root offset = 0 (invalid/empty)
            buildFs.Write(BitConverter.GetBytes(0u)); // root size = 0
            buildFs.Position = headerOffset + 0x7EC;
            buildFs.Write(MagicId);
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
        var ranges = Xdvdfs.GetXisoRanges(fs, 0, true, false);
        Assert.Empty(ranges);
    }
}
