using System.IO;

namespace BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;

/// <summary>
/// Represents an ISO stream that provides functionality for reading ISO file structures
/// and accessing data within the ISO at a low level. This includes managing the
/// file stream, reading file entries, and retrieving file data.
/// </summary>
public class IsoSt : IDisposable
{
    public const int SectorSize = 2048;
    private readonly FileStream _fileStream;
    public long VolumeOffset { get; set; }
    internal BinaryReader Reader { get; }

    public IsoSt(string isoPath)
    {
        _fileStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Reader = new BinaryReader(_fileStream);
    }

    public int Read(FileEntry entry, Span<byte> buffer, long offset)
    {
        var fileOffset = VolumeOffset + (long)entry.StartSector * SectorSize + offset;

        if (fileOffset >= _fileStream.Length) return 0;

        _fileStream.Seek(fileOffset, SeekOrigin.Begin);
        return _fileStream.Read(buffer);
    }

    public FileEntry? ReadFileEntry(long sector, long offset)
    {
        var position = VolumeOffset + sector * SectorSize + offset;
        if (position >= _fileStream.Length) return null;

        _fileStream.Seek(position, SeekOrigin.Begin);
        var entry = new FileEntry();
        try
        {
            entry.ReadInternal(Reader, sector, offset);
            return entry;
        }
        catch
        {
            return null;
        }
    }

    public void ExecuteLocked(Action<BinaryReader> action)
    {
        action(Reader);
    }

    public void Dispose()
    {
        Reader.Dispose();
        _fileStream.Dispose();
        GC.SuppressFinalize(this);
    }
}