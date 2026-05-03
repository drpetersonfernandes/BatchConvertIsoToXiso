using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.XisoServices;

public class IsoStTests
{
    [Fact]
    public void ConstructorOpensFileStream()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);

        using var isoSt = new IsoSt(path);
        Assert.NotNull(isoSt);
        Assert.Equal(0, isoSt.VolumeOffset);
    }

    [Fact]
    public void ReadReturnsZeroWhenOffsetBeyondFileLength()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[100]);

        using var isoSt = new IsoSt(path);
        var entry = new FileEntry
        {
            StartSector = 1000 // way beyond file
        };
        var buffer = new byte[10];
        var result = isoSt.Read(entry, buffer.AsSpan(), 0);

        Assert.Equal(0, result);
    }

    [Fact]
    public void ReadReadsDataSuccessfully()
    {
        var path = Path.GetTempFileName();
        var data = new byte[IsoSt.SectorSize * 2];
        data[100] = 42;
        File.WriteAllBytes(path, data);

        using var isoSt = new IsoSt(path);
        var entry = new FileEntry
        {
            StartSector = 0
        };
        var buffer = new byte[256];
        var result = isoSt.Read(entry, buffer.AsSpan(), 100);

        Assert.Equal(256, result);
        Assert.Equal(42, buffer[0]);
    }

    [Fact]
    public void ReadFileEntryReturnsNullWhenPositionBeyondFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[100]);

        using var isoSt = new IsoSt(path);
        var result = isoSt.ReadFileEntry(1000, 0);

        Assert.Null(result);
    }

    [Fact]
    public void ExecuteLockedExecutesActionWithLock()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[100]);

        using var isoSt = new IsoSt(path);
        var executed = false;
        isoSt.ExecuteLocked(_ => { executed = true; });

        Assert.True(executed);
    }

    [Fact]
    public void DisposeReleasesResources()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[100]);

        var isoSt = new IsoSt(path);
        isoSt.Dispose();

        // After dispose, the file should be deletable (FileStream released)
        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
