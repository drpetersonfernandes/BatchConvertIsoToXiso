using System.IO;

namespace BatchConvertIsoToXiso.Services;

public static class TempFolderCleanupHelper
{
    /// <summary>
    /// Deletes a directory with retry logic for locked files
    /// </summary>
    public static async Task<bool> TryDeleteDirectoryWithRetryAsync(string directoryPath, int maxRetries, int delayMs, ILogger logger)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                    return true;

                // Attempt to release any stale file locks
                await TryReleaseFileLocksAsync(directoryPath, logger);

                Directory.Delete(directoryPath, true);
                logger.LogMessage($"Successfully deleted temp folder: {Path.GetFileName(directoryPath)}");
                return true;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                logger.LogMessage($"Deletion attempt {attempt}/{maxRetries} failed for '{Path.GetFileName(directoryPath)}' (files locked). Retrying...");
                await Task.Delay(delayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < maxRetries)
            {
                logger.LogMessage($"Deletion attempt {attempt}/{maxRetries} failed for '{Path.GetFileName(directoryPath)}' (access denied). Retrying...");
                await Task.Delay(delayMs);
            }
            catch (Exception ex)
            {
                logger.LogMessage($"Failed to delete '{Path.GetFileName(directoryPath)}': {ex.Message}");
                return false;
            }
        }

        logger.LogMessage($"WARNING: Could not delete '{Path.GetFileName(directoryPath)}' after {maxRetries} attempts. Manual cleanup may be needed.");
        return false;
    }

    private static Task TryReleaseFileLocksAsync(string directoryPath, ILogger logger)
    {
        try
        {
            var files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try
                {
                    // Attempt to open file to clear stale locks
                    using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                }
                catch
                {
                    // Ignore - file is legitimately locked
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogMessage($"Error during lock release for '{Path.GetFileName(directoryPath)}': {ex.Message}");
        }

        return Task.CompletedTask;
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
