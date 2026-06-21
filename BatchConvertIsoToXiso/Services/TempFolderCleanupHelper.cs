using System.IO;
using BatchConvertIsoToXiso.Interfaces;

namespace BatchConvertIsoToXiso.Services;

public static class TempFolderCleanupHelper
{
    /// <summary>
    /// Deletes a directory with retry logic for locked files
    /// </summary>
    public static async Task TryDeleteDirectoryWithRetryAsync(string directoryPath, int maxRetries, int delayMs, ILogger? logger, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (!Directory.Exists(directoryPath)) return;

                Directory.Delete(directoryPath, true);
                logger?.LogMessage($"Successfully deleted temp folder: {Path.GetFileName(directoryPath)}");
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                logger?.LogMessage($"Deletion attempt {attempt}/{maxRetries} failed for '{Path.GetFileName(directoryPath)}' (files locked). Retrying...");
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < maxRetries)
            {
                logger?.LogMessage($"Deletion attempt {attempt}/{maxRetries} failed for '{Path.GetFileName(directoryPath)}' (access denied). Retrying...");
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogMessage($"Failed to delete '{Path.GetFileName(directoryPath)}': {ex.Message}");
                return;
            }
        }

        logger?.LogMessage($"WARNING: Could not delete '{Path.GetFileName(directoryPath)}' after {maxRetries} attempts. Manual cleanup may be needed.");
    }

    /// <summary>
    /// Cleans up all BatchConvertIsoToXiso temp folders on all fixed drives
    /// </summary>
    public static async Task CleanupBatchConvertTempFoldersAsync(ILogger logger, CancellationToken cancellationToken = default)
    {
        const string searchPattern = "BatchConvertIsoToXiso_*";

        // Collect all unique roots to scan: system temp + all fixed drives
        var rootsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            rootsToScan.Add(Path.GetTempPath());
        }
        catch
        {
            // ignored
        }

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive is { IsReady: true, DriveType: DriveType.Fixed })
                    rootsToScan.Add(drive.Name);
            }
        }
        catch
        {
            // Ignore drive enumeration errors
        }

        foreach (var root in rootsToScan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var directories = Directory.EnumerateDirectories(root, searchPattern, SearchOption.TopDirectoryOnly);
                foreach (var dir in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    logger.LogMessage($"Cleaning up orphaned temp folder: {Path.GetFileName(dir)}");
                    await TryDeleteDirectoryWithRetryAsync(dir, 3, 1000, logger, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogMessage($"Error enumerating temp folders on {root}: {ex.Message}");
            }
        }
    }
}