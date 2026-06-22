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

    // Sentinel value indicating no child entry in directory tree (XISO_PAD_SHORT in extract-xiso.c)
    private const ushort NoChildSentinel = 0xFFFF;

    // Windows-1252 encoding for Xbox filenames (supports accented characters)
    private static readonly Encoding Win1252 = Encoding.GetEncoding(1252);

    /// <summary>
    /// Known XDVDFS partition offsets for different Xbox game disc formats.
    /// Uses shared constant from VolumeDescriptor for consistency.
    /// </summary>
    private static readonly long[] XdvfsOffsets = VolumeDescriptor.GamePartitionOffsets;

    // Filler pattern used to locate system update boundary in XGD3 video partitions
    // Matches XboxKit's FILLER constant: "ABCDABCDABCDABCD"
    private static readonly byte[] SystemUpdateFiller = "ABCDABCDABCDABCD"u8.ToArray();

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
        var scanned = ScanForSignature(isoFs);
        if (scanned.HasValue)
            return scanned;

        // Final fallback: try Sector 0 (rebuilt XISO without video partition header)
        // VolumeDescriptor.ReadFrom() handles this case, so FindXisoSignatureOffset should too
        if (TryValidateVolumeAtSector0(isoFs))
            return 0;

        return null;
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
            if (signature != XdvdfsSignature)
                return false;

            // Check secondary signature at offset + 0x7EC (matching xdvdfs and extract-xiso references)
            isoFs.Seek(offset + XisoHeaderOffset + 0x7EC, SeekOrigin.Begin);
            var secondaryBuffer = new byte[20];
            if (isoFs.Read(secondaryBuffer, 0, 20) != 20)
                return false;

            if (Encoding.ASCII.GetString(secondaryBuffer) != XdvdfsSignature)
                return false;

            // Validate root directory exists and has valid parameters
            isoFs.Seek(offset + XisoHeaderOffset + 20, SeekOrigin.Begin);
            var rootOffset = Utils.ReadUInt(isoFs);
            var rootSize = Utils.ReadUInt(isoFs);

            // Empty root directory is valid (rootOffset == 0 && rootSize == 0)
            // Non-empty root must have valid offset and size within file bounds
            if (rootOffset == 0 && rootSize == 0)
                return true;

            // Root size should be reasonable (not 0, not larger than file)
            var rootOffsetBytes = rootOffset * Utils.SectorSize;
            if (rootSize == 0 || rootSize > fileLength || offset + rootOffsetBytes + rootSize > fileLength)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates that a valid XDVDFS volume exists at Sector 0 (rebuilt XISO without video partition).
    /// For rebuilt XISOs, the volume descriptor starts at byte offset 0 instead of Sector 32.
    /// </summary>
    private static bool TryValidateVolumeAtSector0(FileStream isoFs)
    {
        try
        {
            if (isoFs.Length < 0x800)
                return false;

            // Check primary signature at byte offset 0
            isoFs.Seek(0, SeekOrigin.Begin);
            var sigBuffer = new byte[20];
            if (isoFs.Read(sigBuffer, 0, 20) != 20)
                return false;

            if (Encoding.ASCII.GetString(sigBuffer) != XdvdfsSignature)
                return false;

            // Check secondary signature at byte offset 0x7EC
            isoFs.Seek(0x7EC, SeekOrigin.Begin);
            var secondaryBuffer = new byte[20];
            if (isoFs.Read(secondaryBuffer, 0, 20) != 20)
                return false;

            if (Encoding.ASCII.GetString(secondaryBuffer) != XdvdfsSignature)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Performs a sector-by-sector scan for XDVDFS signature.
    /// Used as fallback when known offsets don't work.
    /// Scan regions cover all known extract-xiso offsets with margin:
    /// - XGD3: 0x02080000 (~32.5MB)
    /// - XGD2: 0x0FD90000 (~253.5MB)
    /// - XGD1: 0x18300000 (~387MB)
    /// - 0x89D80000 (~2.2GB)
    /// </summary>
    private static long? ScanForSignature(FileStream isoFs)
    {
        var signatureBytes = Encoding.ASCII.GetBytes(XdvdfsSignature);
        var fileLength = isoFs.Length;

        // Scan regions covering all known XGD offsets with margin for variants
        var scanRegions = new[]
        {
            (start: 0x10000L, end: Math.Min(fileLength, 0x20000000L)), // 64KB to 512MB (covers XGD3, XGD2)
            (start: 0x18000000L, end: Math.Min(fileLength, 0x20000000L)), // 384MB to 512MB (covers XGD1)
            (start: 0x80000000L, end: Math.Min(fileLength, 0xB0000000L)) // 2GB to 2.75GB (covers 0x89D80000)
        };

        foreach (var (start, end) in scanRegions)
        {
            if (start >= fileLength)
                continue;

            isoFs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[Utils.SectorSize * 16]; // Read 16 sectors at a time for efficiency
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
                    if (buffer.AsSpan(i, signatureBytes.Length).SequenceEqual(signatureBytes))
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

            // Empty root directory is valid (rootOffset == 0 && rootSize == 0)
            if (rootOffset == 0 && rootSize == 0)
                return true;

            // Non-empty root must have valid offset and size within file bounds
            var rootOffsetBytes = rootOffset * Utils.SectorSize;
            if (rootSize == 0 || offset + rootOffsetBytes + rootSize > isoFs.Length)
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

    // Traverse file tree to get all valid data sectors in XISO using an iterative approach.
    // Note: Uses stack-based iteration (right → current → left) instead of XboxKit's recursive
    // left → current → right. Both produce the same valid sectors set since the final result
    // is sorted and deduplicated. The iterative approach adds cycle detection via visited HashSet
    // for robustness against corrupted trees.
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

            try
            {
                // Read LeftSubTree offset.
                // 0xFFFF means no left child — it does NOT mean the current entry is absent.
                var leftChildOffset = Utils.ReadUShort(isoFs);

                // Always read the full entry regardless of left child status.
                var rightChildOffset = Utils.ReadUShort(isoFs);
                var entryOffsetRaw = Utils.ReadUInt(isoFs);
                var entryOffset = entryOffsetRaw * Utils.SectorSize;
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
                        fileName = Win1252.GetString(nameBuffer);
                    }
                }

                var isDirectory = (attributes & 0x10) != 0;

                // Push Right Child to stack (process after current entry)
                // 0xFFFF = no entry (padding), 0 = no child (valid but empty)
                if (rightChildOffset != NoChildSentinel && rightChildOffset != 0)
                {
                    stack.Push(item with { ChildOffset = (long)rightChildOffset * 4 });
                }

                // Process Current Entry
                if (isDirectory)
                {
                    // Skip $SystemUpdate directory entirely if requested (matching extract-xiso -s flag)
                    // The directory entry itself remains in the parent's table sectors (already counted),
                    // but we skip traversing into its subdirectory table.
                    if (skipSystemUpdate && fileName.Equals("$SystemUpdate", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!quiet) Debug.WriteLine("Skipping $SystemUpdate directory and contents.");
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
                // 0xFFFF = no entry (padding), 0 = no child (valid but empty)
                if (leftChildOffset != NoChildSentinel && leftChildOffset != 0)
                {
                    stack.Push(item with { ChildOffset = (long)leftChildOffset * 4 });
                }
            }
            catch (EndOfStreamException)
            {
                // Corrupted ISO: stop traversal and return partial results
                if (!quiet) Debug.WriteLine("EndOfStreamException encountered — returning partial sector list.");
                break;
            }
        }
    }

    public static List<(uint Start, uint End)> GetXisoRanges(FileStream isoFs, long offset, bool quiet, bool skipSystemUpdate)
    {
        var validSectors = new List<uint>();
        var headerOffset = offset + XisoHeaderOffset;
        var ranges = new List<(uint, uint)>();

        // Validate Header Signature — return empty list if invalid (matching XboxKit behavior)
        isoFs.Seek(headerOffset, SeekOrigin.Begin);
        var signatureBuffer = new byte[20];
        if (isoFs.Read(signatureBuffer, 0, 20) != 20)
        {
            return ranges;
        }

        var signature = Encoding.ASCII.GetString(signatureBuffer);
        if (signature != XdvdfsSignature)
        {
            return ranges;
        }

        // Add Header sectors (Standard XISO behavior)
        var headerOffsetSector = headerOffset / Utils.SectorSize;
        validSectors.Add((uint)headerOffsetSector);
        validSectors.Add((uint)headerOffsetSector + 1);

        // Read Root Directory Info
        isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
        var rootOffset = Utils.ReadUInt(isoFs);
        var rootSize = Utils.ReadUInt(isoFs);

        // Guard against empty or invalid filesystem
        if (rootSize == 0) return ranges;

        var visited = new HashSet<long>();
        GetValidSectors(isoFs, offset, validSectors, rootOffset * Utils.SectorSize, rootSize, quiet, skipSystemUpdate, visited);

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

    /// <summary>
    /// Locates the system update file offset in XGD3 video partitions.
    /// Searches backwards from the end of the file for the filler pattern
    /// "ABCDABCDABCDABCD" which marks the boundary of the system update area.
    /// Based on XboxKit's SUOffset implementation.
    /// </summary>
    /// <param name="videoFs">The video partition file stream.</param>
    /// <returns>The offset where the system update area begins, or file length if not found.</returns>
    public static long GetSystemUpdateOffset(FileStream videoFs)
    {
        var updateOffset = videoFs.Length;
        var videoBuf = new byte[16];

        while (updateOffset >= Utils.SectorSize)
        {
            videoFs.Seek(updateOffset - Utils.SectorSize, SeekOrigin.Begin);
            var bytesRead = 0;
            while (bytesRead < videoBuf.Length)
            {
                var n = videoFs.Read(videoBuf, bytesRead, videoBuf.Length - bytesRead);
                if (n == 0) break;

                bytesRead += n;
            }

            if (videoBuf.AsSpan().SequenceEqual(SystemUpdateFiller))
                break;

            updateOffset -= Utils.SectorSize;
        }

        return updateOffset;
    }
}
