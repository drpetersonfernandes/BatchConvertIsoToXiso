using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;
using BatchConvertIsoToXiso.Services.XisoServices.XDVDFS;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.XisoServices;

public class VolumeDescriptorTests : IDisposable
{
    private static readonly byte[] MagicId = "MICROSOFT*XBOX*MEDIA"u8.ToArray();
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private string CreateValidIsoFile(long gamePartitionOffset = 0)
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        // Write zeros up to the volume descriptor area
        const int volumeDescriptorSector = 32;
        const int sectorSize = 2048;
        var volumePosition = gamePartitionOffset + (long)volumeDescriptorSector * sectorSize;

        // Ensure file is large enough
        var requiredSize = volumePosition + 0x800;
        fs.SetLength(requiredSize);

        fs.Position = volumePosition;

        // Write Magic ID 1 (20 bytes)
        fs.Write(MagicId);

        // Write RootDirTableSector (4 bytes)
        fs.Write(BitConverter.GetBytes(20u));

        // Pad to offset 0x7EC from header start
        fs.Position = volumePosition + 0x7EC;

        // Write Magic ID 2 (20 bytes)
        fs.Write(MagicId);

        return path;
    }

    private string CreateInvalidIsoFile()
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(1024 * 1024); // 1MB
        // Leave as zeros - invalid

        return path;
    }

    [Fact]
    public void ReadFromWithValidStandardVolumeDescriptorReturnsVolumeDescriptor()
    {
        var path = CreateValidIsoFile();
        using var isoSt = new IsoSt(path);

        var vol = VolumeDescriptor.ReadFrom(isoSt);

        Assert.NotNull(vol);
        Assert.Equal(20u, vol.RootDirTableSector);
        Assert.Equal(0, isoSt.VolumeOffset);
    }

    [Fact]
    public void ReadFromWithValidDualLayerVolumeDescriptorReturnsVolumeDescriptor()
    {
        const long gamePartitionOffset = 2048L * 32 * 6192;
        var path = CreateValidIsoFile(gamePartitionOffset);
        using var isoSt = new IsoSt(path);

        var vol = VolumeDescriptor.ReadFrom(isoSt);

        Assert.NotNull(vol);
        Assert.Equal(20u, vol.RootDirTableSector);
        Assert.Equal(gamePartitionOffset, isoSt.VolumeOffset);
    }

    [Fact]
    public void ReadFromWithValidRebuiltXisoReturnsVolumeDescriptor()
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        using (var buildFs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            // Rebuilt XISO: volume descriptor at sector 0, offset 0
            const int headerOffset = 0x10000;
            buildFs.SetLength(headerOffset + 0x800);

            buildFs.Position = headerOffset;
            buildFs.Write(MagicId);
            buildFs.Write(BitConverter.GetBytes(10u));
            buildFs.Position = headerOffset + 0x7EC;
            buildFs.Write(MagicId);
        }

        using var isoSt = new IsoSt(path);

        var vol = VolumeDescriptor.ReadFrom(isoSt);

        Assert.NotNull(vol);
        Assert.Equal(10u, vol.RootDirTableSector);
        Assert.Equal(0, isoSt.VolumeOffset);
    }

    [Fact]
    public void ReadFromWithInvalidIsoThrowsInvalidDataException()
    {
        var path = CreateInvalidIsoFile();
        using var isoSt = new IsoSt(path);

        Assert.Throws<InvalidDataException>(() => VolumeDescriptor.ReadFrom(isoSt));
    }
}
