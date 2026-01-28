using System.Diagnostics;
using System.IO;
using System.Text;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;

namespace BatchConvertIsoToXiso.Services.XisoServices.XDVDFS;

/// <summary>
/// Provides functionality for processing and extracting valid data ranges
/// from an XISO (Xbox ISO) file based on XDVDFS traversal logic.
/// </summary>
internal static class Xdvdfs
{
    private const long XisoHeaderOffset = 0x10000;
    public static readonly byte[] Magic = "XBOX_DVD_LAYOUT_TOOL_SIG"u8.ToArray();

    private struct DirectoryWorkItem
    {
        public long RootOffset;
        public uint RootSize;
        public long ChildOffset;
    }

    // Traverse file tree to get all valid data sectors in XISO using an iterative approach
    private static void GetValidSectors(FileStream isoFs, long isoOffset, List<uint> validSectors,
        long rootOffset, uint rootSize, bool quiet, bool skipSystemUpdate, HashSet<long> visited)
    {
        var stack = new Stack<DirectoryWorkItem>();
        stack.Push(new DirectoryWorkItem { RootOffset = rootOffset, RootSize = rootSize, ChildOffset = 0 });

        while (stack.Count > 0)
        {
            var item = stack.Pop();

            if (item.ChildOffset >= item.RootSize) continue;

            var cur = isoOffset + item.RootOffset + item.ChildOffset;

            // Cycle detection
            if (!visited.Add(cur)) continue;

            // Add the directory table sectors themselves as valid, but only once per table.
            if (item.ChildOffset == 0)
            {
                var curOffset = cur / Utils.SectorSize;
                var curSize = (item.RootSize + Utils.SectorSize - 1) / Utils.SectorSize;
                for (var i = curOffset; i < curOffset + curSize; i++)
                    validSectors.Add((uint)i);
            }

            isoFs.Seek(cur, SeekOrigin.Begin);

            // Read LeftSubTree
            var leftChildOffset = Utils.ReadUShort(isoFs);

            // Check for empty directory (at start offset with 0xFFFF)
            if (leftChildOffset == 0xFFFF && item.ChildOffset == 0)
            {
                continue;
            }

            // Continue reading the rest of the entry
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

            // Push Right Child to stack
            if (rightChildOffset != 0xFFFF && rightChildOffset != 0)
            {
                stack.Push(item with { ChildOffset = (long)rightChildOffset * 4 });
            }

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
                    // Push Subdirectory to stack
                    stack.Push(new DirectoryWorkItem
                    {
                        RootOffset = entryOffset,
                        RootSize = entrySize,
                        ChildOffset = 0
                    });
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

            // Push Left Child to stack
            if (leftChildOffset != 0xFFFF && leftChildOffset != 0 && item.ChildOffset != leftChildOffset * 4)
            {
                stack.Push(item with { ChildOffset = (long)leftChildOffset * 4 });
            }
        }
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

        var visited = new HashSet<long>();
        GetValidSectors(isoFs, offset, validSectors, (long)rootOffset * Utils.SectorSize, rootSize, quiet, skipSystemUpdate, visited);

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
