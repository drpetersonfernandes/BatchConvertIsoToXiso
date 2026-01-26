using System.IO;
using System.Text;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Models.Xiso;

namespace BatchConvertIsoToXiso.Services.Xiso.Writing;

public class XisoWriter
{
    private const int SectorSize = 2048;
    private const uint RootDirSector = 0x108; // Standard XISO root sector

    private readonly ILogger _logger;

    public XisoWriter(ILogger logger)
    {
        _logger = logger;
    }

    public Task<bool> RewriteIsoAsync(string sourcePath, string destPath, bool skipSystemUpdate, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        return Task.Run(() =>
        {
            try
            {
                using var sourceSt = new IsoSt(sourcePath);
                var volume = VolumeDescriptor.ReadFrom(sourceSt);
                var sourceRoot = FileEntry.CreateRootEntry(volume.RootDirTableSector);

                // 1. Build AVL Tree from Source
                progress.Report(new BatchOperationProgress { StatusText = "Analyzing filesystem structure..." });
                var newRoot = new AvlNode
                {
                    FileName = "", // Root has no name
                    Attributes = XisoFsFileAttributes.Directory,
                    StartSector = RootDirSector
                };

                BuildTreeRecursive(sourceRoot, sourceSt, ref newRoot.Subdirectory, skipSystemUpdate);

                // 2. Calculate Layout
                progress.Report(new BatchOperationProgress { StatusText = "Calculating new layout..." });
                var currentSector = RootDirSector;

                // Calculate size of root directory table
                long rootDirSize = 0;
                CalculateDirectorySize(newRoot.Subdirectory, ref rootDirSize);
                newRoot.FileSize = rootDirSize;

                // Root directory table starts at RootDirSector
                // Files start after the root directory table
                currentSector += NumberOfSectors(newRoot.FileSize);

                // Calculate offsets for all subdirectories and files
                CalculateOffsets(newRoot.Subdirectory, ref currentSector);

                // 3. Write New ISO
                progress.Report(new BatchOperationProgress { StatusText = "Writing optimized ISO..." });
                using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var writer = new BinaryWriter(destStream);

                // Write Header (Sector 0 to 32)
                WriteVolumeHeader(writer, newRoot.StartSector, (uint)newRoot.FileSize);

                // Write Root Directory Table
                WriteDirectoryTable(writer, newRoot.Subdirectory, newRoot.StartSector);

                // Write Files and Subdirectories
                long totalBytesWritten = 0;
                WriteTreeData(newRoot.Subdirectory, sourceSt, writer, ref totalBytesWritten, progress, token);

                // Pad to end of sector
                PadToSector(writer);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Rewrite failed: {ex.Message}");
                return false;
            }
        }, token);
    }

    private static void BuildTreeRecursive(FileEntry sourceDir, IsoSt isoSt, ref AvlNode? destTreeRoot, bool skipSystemUpdate)
    {
        var firstChild = sourceDir.GetFirstChild(isoSt);
        if (firstChild == null) return;

        var stack = new Stack<FileEntry>();
        var current = firstChild;
        var visited = new HashSet<(long, long)>();

        while (current != null || stack.Count > 0)
        {
            while (current != null)
            {
                if (!visited.Add((current.EntrySector, current.EntryOffset)))
                {
                    current = null;
                    continue;
                }

                stack.Push(current);
                current = current.HasLeftChild ? current.GetLeftChild(isoSt) : null;
            }

            if (stack.Count == 0) break;

            current = stack.Pop();

            // Process Node
            if (!string.IsNullOrEmpty(current.FileName))
            {
                if (!skipSystemUpdate || !current.FileName.StartsWith("$SystemUpdate", StringComparison.OrdinalIgnoreCase))
                {
                    var newNode = new AvlNode
                    {
                        FileName = current.FileName,
                        FileSize = current.FileSize,
                        OldStartSector = current.StartSector,
                        Attributes = current.Attributes
                    };

                    var root = destTreeRoot;
                    AvlTree.Insert(ref root, newNode);
                    destTreeRoot = root;

                    if (current.IsDirectory)
                    {
                        BuildTreeRecursive(current, isoSt, ref newNode.Subdirectory, skipSystemUpdate);
                    }
                }
            }

            current = current.HasRightChild ? current.GetRightChild(isoSt) : null;
        }
    }

    private static void CalculateDirectorySize(AvlNode? node, ref long size)
    {
        while (true)
        {
            if (node == null) return;

            // In-order traversal to calculate size
            CalculateDirectorySize(node.Left, ref size);

            // Calculate entry size
            var entrySize = 14 + (uint)node.FileName.Length;
            entrySize += (4 - entrySize % 4) % 4; // Align to 4 bytes

            // Check if this entry pushes us to a new sector
            if (NumberOfSectors(size + entrySize) > NumberOfSectors(size))
            {
                // Pad to next sector
                size += (SectorSize - size % SectorSize) % SectorSize;
            }

            node.DirectoryTableOffset = (uint)size;
            size += entrySize;

            node = node.Right;
        }
    }

    private static void CalculateOffsets(AvlNode? node, ref uint currentSector)
    {
        while (true)
        {
            if (node == null) return;

            if (node.Subdirectory != null)
            {
                // This is a directory
                node.StartSector = currentSector;
                long dirSize = 0;
                CalculateDirectorySize(node.Subdirectory, ref dirSize);
                node.FileSize = dirSize;

                currentSector += NumberOfSectors(dirSize);

                // Recurse into children
                CalculateOffsets(node.Subdirectory, ref currentSector);
            }
            else
            {
                // This is a file
                node.StartSector = currentSector;
                currentSector += NumberOfSectors(node.FileSize);
            }

            // Traverse siblings
            CalculateOffsets(node.Left, ref currentSector);
            node = node.Right;
        }
    }

    private static void WriteVolumeHeader(BinaryWriter writer, uint rootSector, uint rootSize)
    {
        // Zero out first 32 sectors (0x0000 - 0x10000)
        // Except for the XISO header at 0x10000

        // We actually start writing at 0x10000 (Sector 32) for the XISO header
        // But we need to fill the file up to that point first?
        // Standard XISO usually has 0s or video partition. We write 0s.
        writer.Write(new byte[0x10000]);

        // Write "MICROSOFT*XBOX*MEDIA"
        writer.Write("MICROSOFT*XBOX*MEDIA"u8.ToArray());

        // Root Dir Sector
        writer.Write(rootSector);

        // Root Dir Size
        writer.Write(rootSize);

        // Filetime (current time)
        writer.Write(DateTime.Now.ToFileTime());

        // Unused padding (0x7c8 bytes)
        writer.Write(new byte[0x7c8]);

        // Trailer "MICROSOFT*XBOX*MEDIA"
        writer.Write("MICROSOFT*XBOX*MEDIA"u8.ToArray());
    }

    private static void WriteDirectoryTable(BinaryWriter writer, AvlNode? root, uint sector)
    {
        if (root == null) return;

        // 1. Calculate total size needed
        // We need to recalculate or store the total size of THIS directory table
        // The root.FileSize stores the size of the directory table if root is a directory node
        // But here 'root' is the first node IN the directory.
        // We need to traverse to find the max extent.
        long totalSize = 0;
        CalculateDirectorySize(root, ref totalSize);

        // 2. Create buffer and fill with 0xFF
        var buffer = new byte[totalSize];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = 0xFF;
        }

        // 3. Write nodes to buffer
        using (var ms = new MemoryStream(buffer))
        using (var bw = new BinaryWriter(ms))
        {
            WriteDirectoryNodeToBuffer(bw, root);
        }

        // 4. Write buffer to ISO
        writer.BaseStream.Seek(sector * SectorSize, SeekOrigin.Begin);
        writer.Write(buffer);
    }

    private static void WriteDirectoryNodeToBuffer(BinaryWriter writer, AvlNode node)
    {
        while (true)
        {
            writer.BaseStream.Seek(node.DirectoryTableOffset, SeekOrigin.Begin);

            var leftOffset = (ushort)(node.Left != null ? node.Left.DirectoryTableOffset / 4 : 0);
            var rightOffset = (ushort)(node.Right != null ? node.Right.DirectoryTableOffset / 4 : 0);

            writer.Write(leftOffset);
            writer.Write(rightOffset);
            writer.Write(node.StartSector);
            writer.Write((uint)node.FileSize);
            writer.Write((byte)node.Attributes);
            writer.Write((byte)node.FileName.Length);
            writer.Write(Encoding.ASCII.GetBytes(node.FileName));

            if (node.Left != null) WriteDirectoryNodeToBuffer(writer, node.Left);
            if (node.Right != null)
            {
                node = node.Right;
                continue;
            }

            break;
        }
    }

    private static void WriteTreeData(AvlNode? node, IsoSt sourceSt, BinaryWriter writer, ref long bytesWritten, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        while (true)
        {
            if (node == null) return;

            // Pre-order traversal to write data
            if (node.Subdirectory != null)
            {
                // Write the directory table for this subdirectory
                WriteDirectoryTable(writer, node.Subdirectory, node.StartSector);

                // Recurse
                WriteTreeData(node.Subdirectory, sourceSt, writer, ref bytesWritten, progress, token);
            }
            else
            {
                // Write file data
                CopyFileData(node, sourceSt, writer, token);
                bytesWritten += node.FileSize;

                // Update progress occasionally
                if (bytesWritten % (10 * 1024 * 1024) == 0) // Every 10MB
                {
                    progress.Report(new BatchOperationProgress { StatusText = $"Writing: {node.FileName}" });
                }
            }

            WriteTreeData(node.Left, sourceSt, writer, ref bytesWritten, progress, token);
            node = node.Right;
        }
    }

    private static void CopyFileData(AvlNode node, IsoSt sourceSt, BinaryWriter writer, CancellationToken token)
    {
        writer.BaseStream.Seek(node.StartSector * SectorSize, SeekOrigin.Begin);

        // Create a dummy FileEntry to use existing IsoSt logic
        var tempEntry = new FileEntry
        {
            StartSector = node.OldStartSector,
            FileSize = (uint)node.FileSize
        };

        var buffer = new byte[1024 * 1024]; // 1MB buffer
        var remaining = node.FileSize;
        long offset = 0;

        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(remaining, buffer.Length);
            var read = sourceSt.Read(tempEntry, buffer.AsSpan(0, toRead), offset);

            writer.Write(buffer, 0, read);

            remaining -= read;
            offset += read;
        }

        // Pad to sector boundary
        PadToSector(writer);
    }

    private static void PadToSector(BinaryWriter writer)
    {
        var pos = writer.BaseStream.Position;
        var padding = (SectorSize - pos % SectorSize) % SectorSize;
        if (padding > 0)
        {
            writer.Write(new byte[padding]); // Write 0s
        }
    }

    private static uint NumberOfSectors(long size)
    {
        return (uint)((size + SectorSize - 1) / SectorSize);
    }
}
