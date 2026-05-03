using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.XisoServices;

public class UtilsTests
{
    [Fact]
    public void ReadUShortReadsCorrectValue()
    {
        using var ms = new MemoryStream([0x34, 0x12]);
        using var fs = CreateFileStreamFromBytes([0x34, 0x12]);

        var result = Utils.ReadUShort(fs);
        Assert.Equal(0x1234, result);
    }

    [Fact]
    public void ReadUShortThrowsOnEndOfStream()
    {
        using var fs = CreateFileStreamFromBytes([0x34]);
        Assert.Throws<EndOfStreamException>(() => Utils.ReadUShort(fs));
    }

    [Fact]
    public void ReadUIntReadsCorrectValue()
    {
        using var fs = CreateFileStreamFromBytes([0x78, 0x56, 0x34, 0x12]);
        var result = Utils.ReadUInt(fs);
        Assert.Equal(0x12345678u, result);
    }

    [Fact]
    public void ReadUIntThrowsOnEndOfStream()
    {
        using var fs = CreateFileStreamFromBytes("xV4"u8.ToArray());
        Assert.Throws<EndOfStreamException>(() => Utils.ReadUInt(fs));
    }

    [Fact]
    public void FillBufferToByteArrayReadsAllBytes()
    {
        using var fs = CreateFileStreamFromBytes([1, 2, 3, 4, 5, 6, 7, 8]);
        var buffer = new byte[4];

        var result = Utils.FillBuffer(fs, buffer, 0);

        Assert.True(result);
        Assert.Equal([1, 2, 3, 4], buffer);
    }

    [Fact]
    public void FillBufferToByteArrayWithOffsetSeeksAndReads()
    {
        using var fs = CreateFileStreamFromBytes([1, 2, 3, 4, 5, 6, 7, 8]);
        var buffer = new byte[2];

        var result = Utils.FillBuffer(fs, buffer, 4);

        Assert.True(result);
        Assert.Equal([5, 6], buffer);
    }

    [Fact]
    public void FillBufferToByteArrayReturnsFalseWhenNotEnoughData()
    {
        using var fs = CreateFileStreamFromBytes([1, 2, 3]);
        var buffer = new byte[5];

        var result = Utils.FillBuffer(fs, buffer, 0);

        Assert.False(result);
        Assert.Equal([1, 2, 3, 0, 0], buffer);
    }

    [Fact]
    public void FillBufferBetweenStreamsCopiesData()
    {
        using var inFs = CreateFileStreamFromBytes([1, 2, 3, 4, 5, 6, 7, 8]);
        using var outFs = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        var buf = new byte[4];

        var result = Utils.FillBuffer(inFs, outFs, 0, 6, buf);

        Assert.True(result);
        outFs.Position = 0;
        var outBytes = new byte[6];
        outFs.ReadExactly(outBytes);
        Assert.Equal([1, 2, 3, 4, 5, 6], outBytes);
    }

    [Fact]
    public void FillBufferBetweenStreamsWithOffsetSeeksAndCopies()
    {
        using var inFs = CreateFileStreamFromBytes([1, 2, 3, 4, 5, 6, 7, 8]);
        using var outFs = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        var buf = new byte[4];

        var result = Utils.FillBuffer(inFs, outFs, 4, 2, buf);

        Assert.True(result);
        outFs.Position = 0;
        var outBytes = new byte[2];
        outFs.ReadExactly(outBytes);
        Assert.Equal([5, 6], outBytes);
    }

    [Fact]
    public void WriteZeroesWritesZeros()
    {
        using var fs = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        var buf = new byte[4];

        Utils.WriteZeroes(fs, 0, 10, buf);

        fs.Position = 0;
        var result = new byte[10];
        fs.ReadExactly(result);
        Assert.All(result, static b => Assert.Equal(0, b));
    }

    [Fact]
    public void WriteZeroesWithOffsetSeeksAndWrites()
    {
        using var fs = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        fs.Write([1, 2, 3, 4, 5]);
        fs.Position = 0;
        var buf = new byte[4];

        Utils.WriteZeroes(fs, 2, 2, buf);

        fs.Position = 0;
        var result = new byte[5];
        fs.ReadExactly(result);
        Assert.Equal([1, 2, 0, 0, 5], result);
    }

    private static FileStream CreateFileStreamFromBytes(byte[] bytes)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, bytes);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
    }
}
