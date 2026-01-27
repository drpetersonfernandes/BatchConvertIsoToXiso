using System.IO;

namespace BatchConvertIsoToXiso.Services;

public static class TempFolderCleanupHelper
{
    /// <summary>
    /// Deletes a directory with retry logic for locked files
    /// </summary>
    public static async Task TryDeleteDirectoryWithRetryAsync(string directoryPath, int maxRetries, int delayMs, ILogger? logger)
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
                await Task.Delay(delayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < maxRetries)
            {
                logger?.LogMessage($"Deletion attempt {attempt}/{maxRetries} failed for '{Path.GetFileName(directoryPath)}' (access denied). Retrying...");
                await Task.Delay(delayMs);
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
    /// Cleans up all BatchConvertIsoToXiso temp folders
    /// </summary>
    public static async Task CleanupBatchConvertTempFoldersAsync(ILogger logger)
    {
        var tempPath = Path.GetTempPath();
        const string searchPattern = "BatchConvertIsoToXiso_*";

        try
        {
            var directories = Directory.EnumerateDirectories(tempPath, searchPattern, SearchOption.TopDirectoryOnly);
            foreach (var dir in directories)
            {
                logger.LogMessage($"Cleaning up orphaned temp folder: {Path.GetFileName(dir)}");
                await TryDeleteDirectoryWithRetryAsync(dir, 3, 1000, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogMessage($"Error enumerating temp folders: {ex.Message}");
        }
    }
}