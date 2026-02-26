using System.IO;
using BatchConvertIsoToXiso.interfaces;
using SharpCompress.Archives;

namespace BatchConvertIsoToXiso.Services;

public class FileExtractorService : IFileExtractor
{
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;

    public FileExtractorService(ILogger logger, IBugReportService bugReportService)
    {
        _logger = logger;
        _bugReportService = bugReportService;
    }

    public async Task<bool> ExtractArchiveAsync(string archivePath, string extractionPath, CancellationToken token)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        _logger.LogMessage($"Starting extraction: {archiveFileName}");
        _logger.LogMessage($"  Extraction target: {extractionPath}");

        try
        {
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
                        if (!fullDestPath.StartsWith(Path.GetFullPath(extractionPath), StringComparison.OrdinalIgnoreCase))
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
        catch (Exception ex)
        {
            // Provide user-friendly message for corrupt archives
            if (ex.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogMessage($"ERROR: {archiveFileName} appears to be corrupt or incomplete. The file may have been damaged during download or transfer. Please re-download the archive and try again.");
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
            if (!isEnvironmentalError &&
                !ex.Message.Contains("Data error", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Invalid archive", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Unsupported archive", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase))
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
        catch (Exception ex)
        {
            _logger.LogMessage($"Error getting uncompressed size of archive {Path.GetFileName(archivePath)}: {ex.Message}");

            // Filter out common archive errors from bug reports
            if (!ex.Message.Contains("Data error", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Invalid archive", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Unsupported archive", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase))
            {
                _ = _bugReportService.SendBugReportAsync($"Error getting uncompressed archive size: {archivePath}. Exception: {ex}");
            }

            throw;
        }
    }
}
