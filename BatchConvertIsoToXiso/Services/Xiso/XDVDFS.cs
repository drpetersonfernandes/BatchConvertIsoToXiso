using System.Diagnostics;
using System.IO;
using System.Text;

namespace BatchConvertIsoToXiso.Services.Xiso;

internal static class Xdvdfs
{
    private const long XisoHeaderOffset = 0x10000;
    public static readonly byte[] Magic = "XBOX_DVD_LAYOUT_TOOL_SIG"u8.ToArray();

    // Traverse file tree to get all valid data sectors in XISO
    private static void GetValidSectors(FileStream isoFs, long isoOffset, List<uint> validSectors, long rootOffset, uint rootSize, long childOffset, bool quiet, bool skipSystemUpdate)
    {
        if (childOffset >= rootSize) return;

        var cur = isoOffset + rootOffset + childOffset;

        // Add the directory table sectors themselves as valid
        var curOffset = cur / Utils.SectorSize;
        var curSize = (rootSize - childOffset + Utils.SectorSize - 1) / Utils.SectorSize;
        for (var i = curOffset; i < curOffset + curSize; i++)
            validSectors.Add((uint)i);

        isoFs.Seek(cur, SeekOrigin.Begin);

        // 0xFFFF is the marker for an empty directory table
        var leftChildOffset = Utils.ReadUShort(isoFs);
        if (leftChildOffset == 0xFFFF) return;

        var rightChildOffset = Utils.ReadUShort(isoFs);
        var entryOffsetRaw = Utils.ReadUInt(isoFs);
        var entryOffset = (long)entryOffsetRaw * Utils.SectorSize;
        var entrySize = Utils.ReadUInt(isoFs);
        var attributes = (byte)isoFs.ReadByte();
        var nameLength = (byte)isoFs.ReadByte();

        var fileName = "";
        if (nameLength > 0)
        {
            var nameBuffer = new byte[nameLength];
            isoFs.ReadExactly(nameBuffer, 0, nameLength);
            fileName = Encoding.ASCII.GetString(nameBuffer);
        }

        var isDirectory = (attributes & 0x10) != 0;

        // Traverse Left Child
        if (leftChildOffset != 0)
            GetValidSectors(isoFs, isoOffset, validSectors, rootOffset, rootSize, (long)leftChildOffset * 4, quiet, skipSystemUpdate);

        // Process Current Entry
        if (isDirectory)
        {
            // Skip contents of $SystemUpdate if requested
            if (skipSystemUpdate && fileName.Equals("$SystemUpdate", StringComparison.OrdinalIgnoreCase))
            {
                if (!quiet) Debug.WriteLine("Skipping $SystemUpdate directory contents.");
            }
            else if (entryOffsetRaw > 0)
            {
                GetValidSectors(isoFs, isoOffset, validSectors, entryOffset, entrySize, 0, quiet, skipSystemUpdate);
            }
        }
        else if (entryOffsetRaw > 0)
        {
            // Add file data sectors
            var fileOffset = (isoOffset + entryOffset) / Utils.SectorSize;
            var fileSize = (entrySize + Utils.SectorSize - 1) / Utils.SectorSize;
            for (var i = fileOffset; i < fileOffset + fileSize; i++)
                validSectors.Add((uint)i);
        }

        // Traverse Right Child
        if (rightChildOffset != 0)
            GetValidSectors(isoFs, isoOffset, validSectors, rootOffset, rootSize, (long)rightChildOffset * 4, quiet, skipSystemUpdate);
    }

    public static List<(uint Start, uint End)> GetXisoRanges(FileStream isoFs, long offset, bool quiet, bool skipSystemUpdate)
    {
        var validSectors = new List<uint>();
        var headerOffset = offset + XisoHeaderOffset;

        // Add Header sectors (Standard XISO behavior)
        var headerOffsetSector = headerOffset / Utils.SectorSize;
        validSectors.Add((uint)headerOffsetSector);
        validSectors.Add((uint)headerOffsetSector + 1);

        isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
        var rootOffset = Utils.ReadUInt(isoFs);
        var rootSize = Utils.ReadUInt(isoFs);

        GetValidSectors(isoFs, offset, validSectors, (long)rootOffset * Utils.SectorSize, rootSize, 0, quiet, skipSystemUpdate);

        var ranges = new List<(uint, uint)>();
        if (validSectors.Count == 0) return ranges;

        var sortedSectors = validSectors.Distinct().OrderBy(static x => x).ToList();
        var start = sortedSectors[0];
        var prev = sortedSectors[0];

        for (var i = 1; i < sortedSectors.Count; i++)
        {
            var current = sortedSectors[i];
            if (current == prev + 1)
            {
                prev = current;
            }
            else
            {
                ranges.Add((start, prev));
                start = current;
                prev = current;
            }
        }

        ranges.Add((start, prev));

        return ranges;
    }
}
