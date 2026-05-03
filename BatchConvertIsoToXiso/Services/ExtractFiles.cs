using System.IO;
using BatchConvertIsoToXiso.interfaces;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace BatchConvertIsoToXiso.Services;

public class FileExtractorService : IFileExtractor
{
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;

    // Cloud file attribute constants
    private const int FileAttributeRecallOnOpen = 0x00040000;
    private const int FileAttributeRecallOnDataAccess = 0x00400000;
    private const int ErrorCloudFileProviderNotRunning = 362;

    public FileExtractorService(ILogger logger, IBugReportService bugReportService)
    {
        _logger = logger;
        _bugReportService = bugReportService;
    }

    /// <summary>
    /// Checks if a file is a cloud file (OneDrive, Dropbox, etc.) and not fully downloaded locally.
    /// </summary>
    private static bool IsCloudFile(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists) return false;

            var attributes = fileInfo.Attributes;
            return (attributes & (FileAttributes)FileAttributeRecallOnOpen) == (FileAttributes)FileAttributeRecallOnOpen ||
                   (attributes & (FileAttributes)FileAttributeRecallOnDataAccess) == (FileAttributes)FileAttributeRecallOnDataAccess;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to ensure a cloud file is hydrated (downloaded locally) before accessing it.
    /// </summary>
    private async Task<bool> EnsureCloudFileHydratedAsync(string filePath, CancellationToken token)
    {
        try
        {
            // Open the file with read access to trigger hydration
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);

            // Read a small portion to ensure the file is fully available
            var buffer = new byte[1];
            _ = await fs.ReadAsync(buffer, token);

            return true;
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070146) || // ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING
                                     ex.Message.Contains("cloud file provider", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines if an exception is related to cloud file provider issues.
    /// </summary>
    private static bool IsCloudFileProviderError(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            // Check for specific cloud file error codes and messages
            if (ioEx.HResult == unchecked((int)0x80070146) || // ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING
                ioEx.HResult == ErrorCloudFileProviderNotRunning)
            {
                return true;
            }

            if (ioEx.Message.Contains("cloud file provider", StringComparison.OrdinalIgnoreCase) ||
                ioEx.Message.Contains("cloud file", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Verifies that the drive containing the specified path is ready.
    /// </summary>
    private void VerifyDriveReady(string filePath)
    {
        try
        {
            var root = Path.GetPathRoot(filePath);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    throw new IOException($"The device is not ready. : '{filePath}'");
                }
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"  Warning: Could not verify drive readiness: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes an asynchronous action with a brief retry on IOException.
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, CancellationToken token, string operationDescription)
    {
        const int maxRetries = 2;
        const int delayMs = 1000;
        int attempt = 0;

        while (true)
        {
            try
            {
                return await action();
            }
            catch (IOException ex) when (attempt < maxRetries)
            {
                attempt++;
                _logger.LogMessage($"  {operationDescription} failed on attempt {attempt}/{maxRetries}: {ex.Message}. Retrying in {delayMs}ms...");
                await Task.Delay(delayMs, token);
            }
        }
    }

    /// <summary>
    /// Executes an asynchronous action with a brief retry on IOException.
    /// </summary>
    private async Task ExecuteWithRetryAsync(Func<Task> action, CancellationToken token, string operationDescription)
    {
        const int maxRetries = 2;
        const int delayMs = 1000;
        int attempt = 0;

        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (IOException ex) when (attempt < maxRetries)
            {
                attempt++;
                _logger.LogMessage($"  {operationDescription} failed on attempt {attempt}/{maxRetries}: {ex.Message}. Retrying in {delayMs}ms...");
                await Task.Delay(delayMs, token);
            }
        }
    }

    /// <summary>
    /// Checks if there is enough disk space on the target drive for the extraction.
    /// </summary>
    private void CheckDiskSpace(string extractionPath, long totalSize, string archiveFileName)
    {
        try
        {
            var fullPath = Path.GetFullPath(extractionPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return;

            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < totalSize)
            {
                var requiredSpace = Formatter.FormatBytes(totalSize);
                var availableSpace = Formatter.FormatBytes(drive.AvailableFreeSpace);
                var errorMessage = $"Not enough disk space to extract {archiveFileName}. Required: {requiredSpace}, Available: {availableSpace}.";
                _logger.LogMessage($"  ERROR: {errorMessage}");
                throw new IOException(errorMessage);
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Ignore other errors during disk space check to avoid blocking extraction if check fails for some reason
            _logger.LogMessage($"  Warning: Could not check disk space: {ex.Message}");
        }
    }

    public async Task<bool> ExtractArchiveAsync(string archivePath, string extractionPath, CancellationToken token)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        _logger.LogMessage($"Starting extraction: {archiveFileName}");
        _logger.LogMessage($"  Extraction target: {extractionPath}");

        try
        {
            // Check for cloud files and attempt to hydrate before extraction
            if (IsCloudFile(archivePath))
            {
                _logger.LogMessage($"  Detected cloud file: {archiveFileName}. Attempting to ensure local availability...");

                var hydrated = await EnsureCloudFileHydratedAsync(archivePath, token);
                if (!hydrated)
                {
                    throw new IOException(
                        $"The cloud file provider is not running. The file '{archivePath}' is stored in a cloud storage service " +
                        "(OneDrive, Dropbox, etc.) but the sync client is not available. Please ensure your cloud storage " +
                        "application is running and the file is fully synchronized before trying again.");
                }

                _logger.LogMessage("  Cloud file is now available locally.");
            }

            _logger.LogMessage($"  Analyzing archive: {archiveFileName}...");

            VerifyDriveReady(archivePath);

            await ExecuteWithRetryAsync(async () =>
            {
                await Task.Run(() =>
                {
                    using var archive = ArchiveFactory.OpenArchive(archivePath);
                    var entries = archive.Entries.Where(static e => !e.IsDirectory).ToList();
                    var fileCount = entries.Count;
                    var totalSize = entries.Sum(static e => e.Size);
                    var archiveFormat = archive.Type;

                    _logger.LogMessage($"  Archive format: {archiveFormat}, Files to extract: {fileCount}, Total size: {Formatter.FormatBytes(totalSize)}");

                    CheckDiskSpace(extractionPath, totalSize, archiveFileName);

                    _logger.LogMessage($"  Extracting files from {archiveFileName}...");

                    var isoExtracted = false;

                    // Manually extract files to prevent "Zip Slip" (absolute paths or path traversal in archives)
                    foreach (var entry in entries)
                    {
                        token.ThrowIfCancellationRequested();

                        var entryPath = entry.Key;

                        // Strict Zip Slip check: Skip suspicious paths entirely
                        if (entryPath != null && (Path.IsPathRooted(entryPath) || entryPath.Contains("..")))
                        {
                            _logger.LogMessage($"  WARNING: Skipping entry '{entryPath}' - potential path traversal (Zip Slip) detected.");
                            continue;
                        }

                        // Check for multiple ISOs
                        if (entryPath != null && Path.GetExtension(entryPath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                        {
                            if (isoExtracted)
                            {
                                _logger.LogMessage($"  Skipping additional ISO: {entryPath} (Only the first ISO is processed per archive).");
                                continue;
                            }

                            isoExtracted = true;
                        }

                        if (entryPath != null)
                        {
                            var fullDestPath = Path.GetFullPath(Path.Combine(extractionPath, entryPath));

                            // Ensure the resulting path is still inside our extraction directory
                            // Fix: base path must end with directory separator to prevent bypass via similar-named directories
                            var basePath = Path.GetFullPath(extractionPath);
                            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                            {
                                basePath += Path.DirectorySeparatorChar;
                            }

                            if (!fullDestPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogMessage($"  WARNING: Skipping entry '{entryPath}' - potential path traversal (Zip Slip) detected.");
                                continue;
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(fullDestPath) ?? throw new InvalidOperationException("fullDestPath cannot be null"));
                            using var fs = new FileStream(fullDestPath, FileMode.Create, FileAccess.Write);
                            entry.WriteTo(fs);
                        }
                    }
                }, token);
            }, token, $"Extraction of {archiveFileName}");

            _logger.LogMessage($"  Successfully extracted: {archiveFileName}");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogMessage($"Extraction of {archiveFileName} was canceled.");
            throw;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Archive"))
        {
            var errorMessage = $"Error extracting {archiveFileName}: Could not open the archive.\n" +
                               "The archive may be corrupted or in an unsupported format.\n" +
                               $"Exception: {ex.Message}";
            _logger.LogMessage($"  {errorMessage}");
            throw;
        }
        catch (Exception ex) when (ex is ArchiveException or ArchiveOperationException)
        {
            var errorMessage = $"Error extracting {archiveFileName}: The archive appears to be invalid, corrupted, or in an unsupported format.\n" +
                               "Please ensure the file is a valid archive (Zip, Rar, 7Zip, etc.).\n" +
                               $"Exception: {ex.Message}";
            _logger.LogMessage($"  {errorMessage}");
            throw new IOException(errorMessage, ex);
        }
        catch (IOException ex) when (ex.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
        {
            var userMessage = $"ERROR: Not enough disk space to extract {archiveFileName}.\n\n" +
                              "Please free up some space on your drive and try again.";
            _logger.LogMessage($"  {userMessage}");
            throw new IOException(userMessage, ex);
        }
        catch (IOException ex) when (IsCloudFileProviderError(ex))
        {
            // Provide user-friendly message for cloud file provider errors
            var userMessage = $"ERROR: Cannot access {archiveFileName} because it is stored in cloud storage (OneDrive, Dropbox, etc.) " +
                              "and the cloud sync provider is not running or the file is not fully synchronized.\n\n" +
                              "Please try:\n" +
                              "1. Ensure your cloud storage application (OneDrive, Dropbox, etc.) is running\n" +
                              "2. Make sure the file is fully downloaded/synced to your local machine\n" +
                              "3. Right-click the file in File Explorer and select 'Always keep on this device'\n" +
                              "4. Try again once the file shows a solid checkmark (not a cloud icon)";

            _logger.LogMessage($"  {userMessage}");

            // Cloud file errors are environmental, do not report as bugs
            throw new IOException(userMessage, ex);
        }
        catch (Exception ex)
        {
            // Provide user-friendly message for corrupt archives
            if (ex.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogMessage($"ERROR: {archiveFileName} appears to be corrupt or incomplete. The file may have been damaged during download or transfer. Please re-download the archive and try again.");
            }
            else if (ex.Message.Contains("Bad state", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogMessage($"ERROR: {archiveFileName} appears to be corrupt (invalid compression data). The file may have been damaged during download or transfer. Please re-download the archive and try again.");
            }
            else if (ex.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogMessage($"ERROR: Not enough disk space to extract {archiveFileName}. Please free up some space on your drive and try again.");
            }
            else
            {
                _logger.LogMessage($"Error extracting {archiveFileName}: {ex.Message}");
            }

            // Filter out environmental/hardware errors (disconnected drives, etc.)
            var isEnvironmentalError = ex is IOException ioEx &&
                                       (ioEx.Message.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("no longer available", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase));

            // Filter out common archive errors (corruption, wrong password, etc.) from bug reports
            // Note: Cloud file errors are now reported as they indicate potential app compatibility issues
            if (!isEnvironmentalError &&
                ex is not ArchiveException &&
                ex is not ArchiveOperationException &&
                !ex.Message.Contains("Data error", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Invalid archive", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Unsupported archive", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Bad state", StringComparison.OrdinalIgnoreCase))
            {
                _ = _bugReportService.SendBugReportAsync($"Error extracting {archiveFileName}. Exception: {ex}");
            }

            throw;
        }
    }

    public async Task<long> GetUncompressedArchiveSizeAsync(string archivePath, CancellationToken token)
    {
        try
        {
            // Check for cloud files and attempt to hydrate before processing
            if (IsCloudFile(archivePath))
            {
                _logger.LogMessage($"  Detected cloud file: {Path.GetFileName(archivePath)}. Attempting to ensure local availability...");

                var hydrated = await EnsureCloudFileHydratedAsync(archivePath, token);
                if (!hydrated)
                {
                    throw new IOException(
                        $"The cloud file provider is not running. The file '{archivePath}' is stored in a cloud storage service " +
                        "(OneDrive, Dropbox, etc.) but the sync client is not available.");
                }
            }

            VerifyDriveReady(archivePath);

            return await ExecuteWithRetryAsync(async () =>
            {
                return await Task.Run(() =>
                {
                    _logger.LogMessage($"  Calculating uncompressed size for: {Path.GetFileName(archivePath)}");

                    using var archive = ArchiveFactory.OpenArchive(archivePath);
                    return archive.Entries.Where(static e => !e.IsDirectory).Sum(static x => x.Size);
                }, token);
            }, token, $"Size calculation for {Path.GetFileName(archivePath)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex) when (IsCloudFileProviderError(ex))
        {
            // Cloud file errors are environmental, do not report as bugs
            _logger.LogMessage($"  Cloud file provider error for {Path.GetFileName(archivePath)}: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error getting uncompressed size of archive {Path.GetFileName(archivePath)}: {ex.Message}");

            // Filter out common archive errors from bug reports
            // Note: Cloud file errors are now reported as they indicate potential app compatibility issues
            if (ex is not ArchiveException &&
                ex is not ArchiveOperationException &&
                !ex.Message.Contains("Data error", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Invalid archive", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Unsupported archive", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Bad state", StringComparison.OrdinalIgnoreCase))
            {
                _ = _bugReportService.SendBugReportAsync($"Error getting uncompressed archive size: {archivePath}. Exception: {ex}");
            }

            throw;
        }
    }
}
