using System.IO;
using System.Text;
using BatchConvertIsoToXiso.Models;

namespace BatchConvertIsoToXiso.Services.Xiso;

public class FileEntry
{
    public long EntrySector { get; internal set; }
    public ushort LeftSubTree { get; internal set; }
    public ushort RightSubTree { get; internal set; }
    public uint StartSector { get; internal set; }
    public uint FileSize { get; internal set; }
    public XisoFsFileAttributes Attributes { get; internal set; }
    public string FileName { get; internal set; } = string.Empty;
    public long EntryOffset { get; set; }
    public bool IsDirectory => (Attributes & XisoFsFileAttributes.Directory) != 0;
    public bool HasLeftChild => LeftSubTree != 0xFFFF;
    public bool HasRightChild => RightSubTree != 0xFFFF;

    public static FileEntry CreateRootEntry(uint rootDirTableSector)
    {
        return new FileEntry
        {
            Attributes = XisoFsFileAttributes.Directory,
            StartSector = rootDirTableSector,
            LeftSubTree = 0xFFFF,
            RightSubTree = 0xFFFF
        };
    }

    internal void ReadInternal(BinaryReader reader, long sector, long offset)
    {
        EntrySector = sector;
        EntryOffset = offset;

        LeftSubTree = reader.ReadUInt16();

        // 0xFFFF indicates an empty directory table.
        // Stop reading immediately to avoid reading garbage or EndOfStream.
        if (LeftSubTree == 0xFFFF) return;

        RightSubTree = reader.ReadUInt16();
        StartSector = reader.ReadUInt32();
        FileSize = reader.ReadUInt32();
        Attributes = (XisoFsFileAttributes)reader.ReadByte();
        var nameLength = reader.ReadByte();

        if (nameLength > 0)
        {
            var nameBytes = reader.ReadBytes(nameLength);
            var rawString = Encoding.ASCII.GetString(nameBytes);
            var nullIndex = rawString.IndexOf('\0');
            FileName = nullIndex >= 0 ? rawString.Substring(0, nullIndex).Trim() : rawString.Trim();
        }

        // Skip padding
        var entrySize = 14 + nameLength;
        var padding = (4 - entrySize % 4) % 4;
        if (padding > 0) reader.BaseStream.Seek(padding, SeekOrigin.Current);
    }

    public FileEntry? GetLeftChild(IsoSt isoSt)
    {
        if (LeftSubTree == 0xFFFF) return null;

        var childOffset = (long)LeftSubTree * 4;
        return childOffset != EntryOffset ? isoSt.ReadFileEntry(EntrySector, childOffset) : null;
    }

    public FileEntry? GetRightChild(IsoSt isoSt)
    {
        if (RightSubTree == 0xFFFF) return null;

        var childOffset = (long)RightSubTree * 4;
        return childOffset != EntryOffset ? isoSt.ReadFileEntry(EntrySector, childOffset) : null;
    }

    public FileEntry? GetFirstChild(IsoSt isoSt)
    {
        return !IsDirectory ? null : isoSt.ReadFileEntry(StartSector, 0);
    }
}