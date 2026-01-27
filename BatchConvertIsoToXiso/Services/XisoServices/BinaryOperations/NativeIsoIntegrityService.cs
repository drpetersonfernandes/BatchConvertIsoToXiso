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

    public Task<bool> TestIsoIntegrityAsync(string isoPath, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        return Task.Run(() =>
        {
            try
            {
                using var isoSt = new IsoSt(isoPath);
                var volume = VolumeDescriptor.ReadFrom(isoSt);
                var rootEntry = FileEntry.CreateRootEntry(volume.RootDirTableSector);

                // Buffer for reading file content (4MB chunks)
                var buffer = new byte[4 * 1024 * 1024];

                return VerifyDirectory(rootEntry, isoSt, buffer, progress, token);
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

    private bool VerifyDirectory(FileEntry dirEntry, IsoSt isoSt, byte[] buffer, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        var children = GetDirectoryEntries(isoSt, dirEntry);

        foreach (var child in children)
        {
            token.ThrowIfCancellationRequested();

            if (child.IsDirectory)
            {
                // Recurse
                if (!VerifyDirectory(child, isoSt, buffer, progress, token)) return false;
            }
            else
            {
                // Report progress for the file being checked
                progress.Report(new BatchOperationProgress { StatusText = $"Verifying: {child.FileName}" });

                // Verify File Content
                if (!VerifyFileContent(child, isoSt, buffer, token))
                {
                    _logger.LogMessage($"Data corruption detected in file: {child.FileName}");
                    return false;
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

            // Read into buffer - we don't do anything with the data, just ensure it reads without exception
            var read = isoSt.Read(file, buffer.AsSpan(0, toRead), currentOffset);

            if (read != toRead)
            {
                return false; // Unexpected end of stream or read failure
            }

            bytesRemaining -= read;
            currentOffset += read;
        }

        return true;
    }

    /// <summary>
    /// Traverses the XISO binary tree to return a flat list of entries for a specific directory.
    /// </summary>
    public List<FileEntry> GetDirectoryEntries(IsoSt isoSt, FileEntry dir)
    {
        var results = new List<FileEntry>();
        var visited = new HashSet<(long, long)>(); // Cycle detection

        var firstChild = dir.GetFirstChild(isoSt);

        // [FIX] Removed "|| firstChild.LeftSubTree == 0xFFFF"
        // A valid file entry can have 0xFFFF as a LeftSubTree.
        // We only return if firstChild is null (which happens if the directory table is truly empty/0-byte).
        if (firstChild == null) return results;

        var stack = new Stack<FileEntry>();
        var current = firstChild;

        // In-order traversal of the binary tree to get directory listing
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

            // [FIX] Ensure we don't add entries with empty names (which might happen if we hit a padding/empty sector entry)
            if (!string.IsNullOrEmpty(current.FileName))
            {
                results.Add(current);
            }

            current = current.HasRightChild ? current.GetRightChild(isoSt) : null;
        }

        return results;
    }
}