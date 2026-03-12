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

    // Standard XDVDFS signature
    private const string XdvdfsSignature = "MICROSOFT*XBOX*MEDIA";

    /// <summary>
    /// Searches for the XDVDFS signature in the ISO file.
    /// Scans at common Redump partition offsets first, then performs a sector-by-sector scan if needed.
    /// </summary>
    /// <param name="isoFs">The ISO file stream to search.</param>
    /// <returns>The offset where the game partition starts, or null if not found.</returns>
    public static long? FindXisoSignatureOffset(FileStream isoFs)
    {
        var signatureBytes = Encoding.ASCII.GetBytes(XdvdfsSignature);
        var fileLength = isoFs.Length;

        // Common Redump game partition offsets to check first (fast path)
        // These are the most common offsets found in Redump Xbox ISOs
        var commonOffsets = new[]
        {
            0x18300000L, // XGD1 standard
            0x0FD90000L, // XGD2 standard
            0x2080000L, // Alternative XGD1
            0x10000L, // Standard XISO (no video partition)
            0x89D80000L // XGD3
        };

        // Check common offsets first (fast path)
        foreach (var offset in commonOffsets)
        {
            if (TryValidateSignatureAtOffset(isoFs, offset, signatureBytes))
            {
                return offset;
            }
        }

        // If not found at common offsets, perform sector-by-sector scan
        // Start from sector 32 (where XISO header typically is) to avoid video partition false positives
        const int sectorSize = 2048;
        const int startSector = 32;
        var maxOffset = Math.Min(fileLength - signatureBytes.Length, 0x10000000L); // Scan up to ~256MB

        isoFs.Seek(startSector * sectorSize, SeekOrigin.Begin);
        var buffer = new byte[sectorSize];
        var position = startSector * sectorSize;

        while (position < maxOffset)
        {
            var bytesRead = isoFs.Read(buffer, 0, sectorSize);
            if (bytesRead < signatureBytes.Length)
                break;

            // Check if signature exists in this sector
            for (var i = 0; i <= bytesRead - signatureBytes.Length; i++)
            {
                if (buffer.Skip(i).Take(signatureBytes.Length).SequenceEqual(signatureBytes))
                {
                    var candidateOffset = position + i - XisoHeaderOffset; // Signature is at header + 0x10000
                    if (candidateOffset >= 0 && TryValidateSignatureAtOffset(isoFs, candidateOffset, signatureBytes))
                    {
                        return candidateOffset;
                    }
                }
            }

            position += bytesRead;
        }

        return null;
    }

    /// <summary>
    /// Validates that a valid XDVDFS signature exists at the specified offset.
    /// Also checks for secondary signature at offset + 0x7EC for additional validation.
    /// </summary>
    private static bool TryValidateSignatureAtOffset(FileStream isoFs, long offset, byte[] signatureBytes)
    {
        try
        {
            var headerPosition = offset + XisoHeaderOffset;
            if (headerPosition + 0x800 > isoFs.Length)
                return false;

            // Check primary signature
            isoFs.Seek(headerPosition, SeekOrigin.Begin);
            var primaryBuffer = new byte[signatureBytes.Length];
            if (isoFs.Read(primaryBuffer, 0, signatureBytes.Length) != signatureBytes.Length)
                return false;

            if (!primaryBuffer.SequenceEqual(signatureBytes))
                return false;

            // Check secondary signature at offset + 0x7EC for additional validation
            isoFs.Seek(headerPosition + 0x7EC, SeekOrigin.Begin);
            var secondaryBuffer = new byte[signatureBytes.Length];
            if (isoFs.Read(secondaryBuffer, 0, signatureBytes.Length) != signatureBytes.Length)
                return false;

            if (!secondaryBuffer.SequenceEqual(signatureBytes))
                return false;

            // Additional validation: check that root directory offset and size are reasonable
            isoFs.Seek(headerPosition + 0x14, SeekOrigin.Begin);
            var rootOffsetBuffer = new byte[4];
            var rootSizeBuffer = new byte[4];
            if (isoFs.Read(rootOffsetBuffer, 0, 4) != 4 || isoFs.Read(rootSizeBuffer, 0, 4) != 4)
                return false;

            var rootOffset = BitConverter.ToUInt32(rootOffsetBuffer, 0);
            var rootSize = BitConverter.ToUInt32(rootSizeBuffer, 0);

            // Root offset should be within file bounds and not unreasonably large
            if (rootOffset == 0 || rootSize == 0)
                return false;

            var rootOffsetBytes = (long)rootOffset * Utils.SectorSize;
            if (offset + rootOffsetBytes + rootSize > isoFs.Length)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

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

            // Safety check: Ensure ChildOffset is within the directory table size
            if (item.ChildOffset >= item.RootSize) continue;

            var cur = isoOffset + item.RootOffset + item.ChildOffset;

            // Cycle detection
            if (!visited.Add(cur)) continue;

            // Add the directory table sectors as valid, but only once per table (when processing root node)
            if (item.ChildOffset == 0)
            {
                var curOffset = cur / Utils.SectorSize;
                var curSize = (item.RootSize + Utils.SectorSize - 1) / Utils.SectorSize;
                for (var i = curOffset; i < curOffset + curSize; i++)
                    validSectors.Add((uint)i);
            }

            // Seek to the current directory entry
            isoFs.Seek(cur, SeekOrigin.Begin);

            // Read LeftSubTree offset.
            // 0xFFFF means no left child — it does NOT mean the current entry is absent.
            var leftChildOffset = Utils.ReadUShort(isoFs);

            // Always read the full entry regardless of left child status.
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
                // Use Read instead of ReadExactly for better compatibility/safety
                var bytesRead = isoFs.Read(nameBuffer, 0, nameLength);
                if (bytesRead == nameLength)
                {
                    fileName = Encoding.ASCII.GetString(nameBuffer);
                }
            }

            var isDirectory = (attributes & 0x10) != 0;

            // Push Right Child to stack (process after current entry)
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
                else if (entryOffsetRaw > 0 && entrySize > 0)
                {
                    // Push subdirectory onto stack for traversal
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
                // Add file data sectors to valid list
                var fileOffset = (isoOffset + entryOffset) / Utils.SectorSize;
                var fileSize = (entrySize + Utils.SectorSize - 1) / Utils.SectorSize;
                for (var i = fileOffset; i < fileOffset + fileSize; i++)
                    validSectors.Add((uint)i);
            }

            // Push Left Child to stack
            if (leftChildOffset != 0xFFFF && leftChildOffset != 0)
            {
                stack.Push(item with { ChildOffset = (long)leftChildOffset * 4 });
            }
        }
    }

    public static List<(uint Start, uint End)> GetXisoRanges(FileStream isoFs, long offset, bool quiet, bool skipSystemUpdate)
    {
        var validSectors = new List<uint>();
        var headerOffset = offset + XisoHeaderOffset;

        // Validate Header Signature
        isoFs.Seek(headerOffset, SeekOrigin.Begin);
        var signatureBuffer = new byte[20];
        if (isoFs.Read(signatureBuffer, 0, 20) != 20)
        {
            throw new EndOfStreamException("Could not read XISO header signature (EOF).");
        }

        var signature = Encoding.ASCII.GetString(signatureBuffer);
        if (!signature.StartsWith(XdvdfsSignature, StringComparison.Ordinal))
        {
            // If signature is invalid, throw exception to be caught by XisoWriter
            throw new InvalidDataException($"Invalid XISO header signature found: '{signature.Trim()}' at offset {headerOffset}. Expected '{XdvdfsSignature}'.");
        }

        // Add Header sectors (Standard XISO behavior)
        var headerOffsetSector = headerOffset / Utils.SectorSize;
        validSectors.Add((uint)headerOffsetSector);
        validSectors.Add((uint)headerOffsetSector + 1);

        // Read Root Directory Info
        isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
        var rootOffset = Utils.ReadUInt(isoFs);
        var rootSize = Utils.ReadUInt(isoFs);

        var ranges = new List<(uint, uint)>();

        // Guard against empty or invalid filesystem
        if (rootSize == 0) return ranges;

        var visited = new HashSet<long>();
        GetValidSectors(isoFs, offset, validSectors, (long)rootOffset * Utils.SectorSize, rootSize, quiet, skipSystemUpdate, visited);

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