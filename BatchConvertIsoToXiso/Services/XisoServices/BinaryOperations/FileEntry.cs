using System.IO;
using System.Text;
using BatchConvertIsoToXiso.Models.XisoDefinitions;

namespace BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;

/// Represents a file entry in the Xbox ISO (XISO) filesystem, including its metadata and structure.
/// A file entry can represent either a file or a directory within the ISO image.
/// It provides methods to navigate the filesystem hierarchy and retrieve child nodes.
/// Properties:
/// - EntrySector: The sector within the ISO where this file entry is located.
/// - LeftSubTree: Reference to the left child file entry in the directory tree, or 0xFFFF if none exists.
/// - RightSubTree: Reference to the right child file entry in the directory tree, or 0xFFFF if none exists.
/// - StartSector: Starting sector of the file's data within the ISO.
/// - FileSize: The size of the file in bytes.
/// - Attributes: The filesystem attributes associated with this file, such as directory or read-only.
/// - FileName: The name of the file or directory.
/// - EntryOffset: The offset of this file entry relative to the start of the ISO.
/// Computed Properties:
/// - IsDirectory: Determines if the entry represents a directory based on its attributes.
/// - HasLeftChild: Indicates whether a left child exists in the directory tree.
/// - HasRightChild: Indicates whether a right child exists in the directory tree.
/// Methods:
/// - CreateRootEntry(uint rootDirTableSector): Creates a root entry with default directory attributes.
/// - ReadInternal(BinaryReader reader, long sector, long offset): Reads and parses the file entry data from a binary stream.
/// - GetLeftChild(IsoSt isoSt): Retrieves the left child of this entry in the directory tree.
/// - GetRightChild(IsoSt isoSt): Retrieves the right child of this entry in the directory tree.
/// - GetFirstChild(IsoSt isoSt): Retrieves the first child entry if this entry represents a directory.
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

        // [FIX] Removed the early return check (if (LeftSubTree == 0xFFFF) return;)
        // We must read the rest of the entry regardless of whether a left child exists.

        LeftSubTree = reader.ReadUInt16();
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
        else
        {
            FileName = string.Empty;
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