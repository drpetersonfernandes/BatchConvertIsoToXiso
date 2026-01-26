using BatchConvertIsoToXiso.Models.Xiso;

namespace BatchConvertIsoToXiso.Services.Xiso.Writing;

public enum AvlSkew
{
    None,
    Left,
    Right
}

public class AvlNode
{
    public string FileName { get; init; } = string.Empty;
    public long FileSize { get; set; }
    public uint StartSector { get; set; }
    public uint OldStartSector { get; init; } // For copying data from source
    public XisoFsFileAttributes Attributes { get; init; }

    // Tree structure - Must be fields to be passed as 'ref'
    public AvlNode? Left;
    public AvlNode? Right;
    public AvlNode? Subdirectory; // If this node is a directory

    public AvlSkew Skew { get; set; } = AvlSkew.None;

    // Layout calculation
    public uint DirectoryTableOffset { get; set; } // Offset in bytes within the directory table
    public long DirectoryStartOffset { get; set; } // Absolute byte offset of the directory table on disc
}