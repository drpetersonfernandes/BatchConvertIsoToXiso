using System.IO;
using BatchConvertIsoToXiso.interfaces;
using SharpCompress.Archives;

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

            await Task.Run(() =>
            {
                using var archive = ArchiveFactory.OpenArchive(archivePath);
                var fileCount = archive.Entries.Count(static e => !e.IsDirectory);
                var archiveFormat = archive.Type;

                _logger.LogMessage($"  Archive format: {archiveFormat}, Files to extract: {fileCount}");
                _logger.LogMessage($"  Extracting files from {archiveFileName}...");

                var isoExtracted = false;

                // Manually extract files to prevent "Zip Slip" (absolute paths or path traversal in archives)
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();

                    if (entry.IsDirectory) continue;

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
            else
            {
                _logger.LogMessage($"Error extracting {archiveFileName}: {ex.Message}");
            }

            // Filter out environmental/hardware errors (disconnected drives, etc.)
            var isEnvironmentalError = ex is IOException ioEx &&
                                       (ioEx.Message.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("no longer available", StringComparison.OrdinalIgnoreCase));

            // Filter out common archive errors (corruption, wrong password, etc.) from bug reports
            // Note: Cloud file errors are now reported as they indicate potential app compatibility issues
            if (!isEnvironmentalError &&
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

            // Verify drive is ready before attempting operation
            try
            {
                var driveLetter = Path.GetPathRoot(archivePath);
                if (!string.IsNullOrEmpty(driveLetter))
                {
                    var driveInfo = new DriveInfo(driveLetter);
                    if (!driveInfo.IsReady)
                    {
                        _logger.LogMessage($"ERROR: Drive {driveLetter} is not ready. Cannot process {Path.GetFileName(archivePath)}");
                        throw new IOException($"The device is not ready. : '{archivePath}'");
                    }
                }
            }
            catch
            {
                /* Ignore drive info errors, let the actual operation fail if needed */
            }

            return await Task.Run(() =>
            {
                _logger.LogMessage($"  Calculating uncompressed size for: {Path.GetFileName(archivePath)}");

                using var archive = ArchiveFactory.OpenArchive(archivePath);
                return archive.Entries.Where(static e => !e.IsDirectory).Sum(static x => x.Size);
            }, token);
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
            if (!ex.Message.Contains("Data error", StringComparison.OrdinalIgnoreCase) &&
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
