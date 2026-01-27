using System.IO;
using BatchConvertIsoToXiso.interfaces;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services.XisoServices.XDVDFS;

namespace BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;

/// <summary>
/// Provides functionality to validate and retrieve file entries from ISO images using the native XISO binary tree format.
/// </summary>
public class NativeIsoIntegrityService : INativeIsoIntegrityService
{
    private readonly ILogger _logger;

    public NativeIsoIntegrityService(ILogger logger)
    {
        _logger = logger;
    }

    public Task<bool> TestIsoIntegrityAsync(string isoPath, bool performDeepScan, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        return Task.Run(() =>
        {
            try
            {
                _logger.LogMessage($"[INFO] Starting structural integrity test for: {Path.GetFileName(isoPath)}");
                _logger.LogMessage("[INFO] Note: This verifies filesystem structure and readability, not data checksums.");

                using var isoSt = new IsoSt(isoPath);

                // 1. Optional Deep Surface Scan: Read entire ISO sequentially to test physical media
                if (performDeepScan)
                {
                    if (!PerformSurfaceScan(isoSt, progress, token))
                    {
                        return false;
                    }

                    // Reset position for structure test
                    isoSt.ExecuteLocked(static reader => reader.BaseStream.Seek(0, SeekOrigin.Begin));
                }

                // 2. Logical Structure Test: Verify each file is readable using queue (BFS)
                var volume = VolumeDescriptor.ReadFrom(isoSt);
                var rootEntry = FileEntry.CreateRootEntry(volume.RootDirTableSector);

                return VerifyAllFiles(rootEntry, isoSt, progress, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Integrity check failed for {Path.GetFileName(isoPath)}: {ex.Message}");
                return false;
            }
        }, token);
    }

    private bool PerformSurfaceScan(IsoSt isoSt, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        _logger.LogMessage("[INFO] Performing deep surface scan (sequential read of all sectors)...");

        var scanSuccessful = false;

        // ExecuteLocked takes an Action (void), so we use a local variable to track success
        isoSt.ExecuteLocked(reader =>
        {
            var stream = reader.BaseStream;
            stream.Seek(0, SeekOrigin.Begin);

            var buffer = new byte[4 * 1024 * 1024]; // 4MB chunks
            var totalBytes = stream.Length;
            long bytesRead = 0;
            long lastReportedPercent = -1;

            while (bytesRead < totalBytes)
            {
                if (token.IsCancellationRequested) return; // Exit the Action

                var toRead = (int)Math.Min(buffer.Length, totalBytes - bytesRead);
                var read = stream.Read(buffer, 0, toRead);

                if (read == 0 && bytesRead < totalBytes)
                {
                    _logger.LogMessage($"[ERROR] Surface scan failed: Unexpected end of file at {bytesRead}.");
                    return; // Exit the Action (scanSuccessful remains false)
                }

                bytesRead += read;

                // Report progress every 1%
                var percent = bytesRead * 100 / totalBytes;
                if (percent > lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    progress.Report(new BatchOperationProgress { StatusText = $"Surface scan: {percent}%" });
                }
            }

            scanSuccessful = true; // If we reached here, the entire file was read
        });

        if (scanSuccessful)
        {
            _logger.LogMessage("[INFO] Surface scan completed successfully.");
        }

        return scanSuccessful;
    }

    private bool VerifyAllFiles(FileEntry rootEntry, IsoSt isoSt, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        // Use a Queue for BFS traversal to avoid stack overflow and process files in order
        var dirQueue = new Queue<FileEntry>();
        dirQueue.Enqueue(rootEntry);

        var buffer = new byte[4 * 1024 * 1024]; // 4MB buffer for file reading

        while (dirQueue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var currentDir = dirQueue.Dequeue();

            var entries = GetDirectoryEntries(isoSt, currentDir);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                if (entry.IsDirectory)
                {
                    dirQueue.Enqueue(entry);
                }
                else
                {
                    progress.Report(new BatchOperationProgress { StatusText = $"Verifying: {entry.FileName}" });

                    if (!VerifyFileContent(entry, isoSt, buffer, token))
                    {
                        _logger.LogMessage($"[ERROR] Data corruption detected in file: {entry.FileName}");
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool VerifyFileContent(FileEntry file, IsoSt isoSt, byte[] buffer, CancellationToken token)
    {
        if (file.FileSize == 0) return true;

        long bytesRemaining = file.FileSize;
        long currentOffset = 0;

        while (bytesRemaining > 0)
        {
            token.ThrowIfCancellationRequested();

            var toRead = (int)Math.Min(buffer.Length, bytesRemaining);
            var read = isoSt.Read(file, buffer.AsSpan(0, toRead), currentOffset);

            if (read != toRead)
            {
                return false;
            }

            bytesRemaining -= read;
            currentOffset += read;
        }

        return true;
    }

    public List<FileEntry> GetDirectoryEntries(IsoSt isoSt, FileEntry dir)
    {
        var results = new List<FileEntry>();
        var visited = new HashSet<(long, long)>();

        var firstChild = dir.GetFirstChild(isoSt);
        if (firstChild == null) return results;

        var stack = new Stack<FileEntry>();
        var current = firstChild;

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

            if (!string.IsNullOrEmpty(current.FileName))
            {
                results.Add(current);
            }

            current = current.HasRightChild ? current.GetRightChild(isoSt) : null;
        }

        return results;
    }
}
