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
    /// Known XDVDFS partition offsets for different Xbox game disc formats.
    /// Based on xdvdfs reference implementation by antangelo.
    /// </summary>
    private static readonly long[] XdvfsOffsets =
    [
        0, // XISO (already trimmed or standard XISO without video partition)
        34078720, // XGD3 (0x02080000) - ~32.5MB offset
        265879552, // XGD2 (0x0FD90000) - ~253.5MB offset
        405798912 // XGD1 (0x18300000) - ~387MB offset
    ];

    /// <summary>
    /// Searches for the XDVDFS signature in the ISO file.
    /// Uses the xdvdfs "try-all with validation" approach - tries all known offsets
    /// and validates by reading the actual volume structure (not just signature).
    /// Falls back to sector scanning for unknown cases.
    /// </summary>
    /// <param name="isoFs">The ISO file stream to search.</param>
    /// <returns>The offset where the game partition starts, or null if not found.</returns>
    public static long? FindXisoSignatureOffset(FileStream isoFs)
    {
        var fileLength = isoFs.Length;

        // Try all known XDVDFS offsets with full validation
        // This is the xdvdfs approach - try each offset and validate by reading the volume
        foreach (var offset in XdvfsOffsets)
        {
            if (offset + XisoHeaderOffset + 0x800 > fileLength)
                continue;

            if (TryValidateVolumeAtOffset(isoFs, offset))
            {
                return offset;
            }
        }

        // Fallback: sector-by-sector scan for non-standard offsets
        // This handles unknown Redump variants or corrupted images
        return ScanForSignature(isoFs);
    }

    /// <summary>
    /// Validates that a valid XDVDFS volume exists at the specified offset.
    /// This performs full validation: signature check + root directory validation.
    /// Based on xdvdfs reference implementation.
    /// </summary>
    private static bool TryValidateVolumeAtOffset(FileStream isoFs, long offset)
    {
        try
        {
            var fileLength = isoFs.Length;

            // Check for valid XDVDFS signature
            isoFs.Seek(offset + XisoHeaderOffset, SeekOrigin.Begin);
            var sigBuffer = new byte[20];
            if (isoFs.Read(sigBuffer, 0, 20) != 20)
                return false;

            var signature = Encoding.ASCII.GetString(sigBuffer);
            if (!signature.StartsWith(XdvdfsSignature, StringComparison.Ordinal))
                return false;

            // Validate root directory exists and has valid parameters
            isoFs.Seek(offset + XisoHeaderOffset + 20, SeekOrigin.Begin);
            var rootOffset = Utils.ReadUInt(isoFs);
            var rootSize = Utils.ReadUInt(isoFs);

            // Root offset should be reasonable (not 0, within file bounds)
            if (rootOffset == 0)
                return false;

            // Root size should be reasonable (not 0, not larger than file)
            var rootOffsetBytes = (long)rootOffset * Utils.SectorSize;
            if (rootSize == 0 || rootSize > fileLength || offset + rootOffsetBytes + rootSize > fileLength)
                return false;

            // Try to read the first directory entry to ensure it's a valid filesystem
            var firstEntryOffset = offset + rootOffsetBytes;
            if (firstEntryOffset + 14 > fileLength)
                return false;

            isoFs.Seek(firstEntryOffset, SeekOrigin.Begin);
            var entryBuffer = new byte[14];
            if (isoFs.Read(entryBuffer, 0, 14) != 14)
                return false;

            // Read entry attributes and name length
            var attributes = entryBuffer[12];
            var nameLength = entryBuffer[13];

            // Validate entry attributes (should be 0x00-0x1F for files, 0x10-0x1F for directories)
            // and name length should be reasonable (1-255 for valid files)
            // ReSharper disable once PatternIsRedundant
            return nameLength is > 0 and <= 255 && (attributes & 0xE0) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Performs a sector-by-sector scan for XDVDFS signature.
    /// Used as fallback when known offsets don't work.
    /// </summary>
    private static long? ScanForSignature(FileStream isoFs)
    {
        var signatureBytes = Encoding.ASCII.GetBytes(XdvdfsSignature);
        var fileLength = isoFs.Length;

        // Scan key regions where game partitions are typically found
        // Video partition on Redump ISOs is usually ~7-10MB, game partition starts after
        const int sectorSize = 2048;
        var scanRegions = new[]
        {
            (start: 0x800000L, end: Math.Min(fileLength, 0x20000000L)), // 8MB to 512MB
            (start: 0x18000000L, end: Math.Min(fileLength, 0x30000000L)), // 384MB to 768MB (XGD1 area)
            (start: 0x80000000L, end: Math.Min(fileLength, 0xA0000000L)) // 2GB to 2.5GB (XGD3 area)
        };

        foreach (var (start, end) in scanRegions)
        {
            if (start >= fileLength)
                continue;

            isoFs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[sectorSize * 16]; // Read 16 sectors at a time for efficiency
            var position = start;

            while (position < end)
            {
                var bytesToRead = (int)Math.Min(buffer.Length, end - position);
                var bytesRead = isoFs.Read(buffer, 0, bytesToRead);
                if (bytesRead < signatureBytes.Length)
                    break;

                // Check if signature exists in this buffer
                for (var i = 0; i <= bytesRead - signatureBytes.Length; i++)
                {
                    if (buffer.Skip(i).Take(signatureBytes.Length).SequenceEqual(signatureBytes))
                    {
                        var candidateOffset = position + i - XisoHeaderOffset;
                        if (candidateOffset >= 0 && TryValidateVolumeAtOffset(isoFs, candidateOffset))
                        {
                            return candidateOffset;
                        }
                    }
                }

                position += bytesRead;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates that a valid XISO signature exists at the specified offset.
    /// This is a public wrapper around TryValidateSignatureAtOffset for external validation.
    /// </summary>
    /// <param name="isoFs">The ISO file stream.</param>
    /// <param name="offset">The offset to check.</param>
    /// <returns>True if valid XISO signature found at the offset, false otherwise.</returns>
    public static bool ValidateXisoSignatureAtOffset(FileStream isoFs, long offset)
    {
        var signatureBytes = Encoding.ASCII.GetBytes(XdvdfsSignature);
        return TryValidateSignatureAtOffset(isoFs, offset, signatureBytes);
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

            // Additional validation: try to read the first file entry in the root directory
            // This ensures we're not hitting a false positive from video partition data
            var firstEntryOffset = offset + rootOffsetBytes;
            if (firstEntryOffset + 14 > isoFs.Length)
                return false;

            try
            {
                isoFs.Seek(firstEntryOffset, SeekOrigin.Begin);
                var entryBuffer = new byte[14];
                if (isoFs.Read(entryBuffer, 0, 14) != 14)
                    return false;

                // Read entry attributes and name length
                var attributes = entryBuffer[12];
                var nameLength = entryBuffer[13];

                // Validate entry attributes (should be 0x00-0x1F for files, 0x10-0x1F for directories)
                // and name length should be reasonable (1-255 for valid files)
                if (nameLength > 0 && (attributes & 0xE0) == 0)
                {
                    // Try to read the filename
                    if (firstEntryOffset + 14 + nameLength <= isoFs.Length)
                    {
                        var nameBuffer = new byte[nameLength];
                        isoFs.Seek(firstEntryOffset + 14, SeekOrigin.Begin);
                        if (isoFs.Read(nameBuffer, 0, nameLength) == nameLength)
                        {
                            var fileName = Encoding.ASCII.GetString(nameBuffer).TrimEnd('\0');
                            // If we can read a non-empty filename, this is likely a real game partition
                            if (!string.IsNullOrWhiteSpace(fileName) && fileName.All(static c => char.IsAscii(c) && !char.IsControl(c)))
                            {
                                return true;
                            }
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
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