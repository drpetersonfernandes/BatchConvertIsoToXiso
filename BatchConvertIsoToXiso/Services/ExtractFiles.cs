using System.IO;
using BatchConvertIsoToXiso.interfaces;
using SevenZip;

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
                using var extractor = new SevenZipExtractor(archivePath);
                var fileCount = extractor.FilesCount;
                var archiveFormat = extractor.Format;

                _logger.LogMessage($"  Archive format: {archiveFormat}, Files to extract: {fileCount}");
                _logger.LogMessage($"  Extracting files from {archiveFileName}...");

                var isoExtracted = false;

                // Manually extract files to prevent "Zip Slip" (absolute paths or path traversal in archives)
                for (var i = 0; i < extractor.FilesCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var fileData = extractor.ArchiveFileData[i];
                    if (fileData.IsDirectory) continue;

                    var entryPath = fileData.FileName;

                    // Strict Zip Slip check: Skip suspicious paths entirely
                    if (Path.IsPathRooted(entryPath) || entryPath.Contains(".."))
                    {
                        _logger.LogMessage($"  WARNING: Skipping entry '{entryPath}' - potential path traversal (Zip Slip) detected.");
                        continue;
                    }

                    // Check for multiple ISOs
                    if (Path.GetExtension(entryPath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isoExtracted)
                        {
                            _logger.LogMessage($"  Skipping additional ISO: {entryPath} (Only the first ISO is processed per archive).");
                            continue;
                        }

                        isoExtracted = true;
                    }

                    var fullDestPath = Path.GetFullPath(Path.Combine(extractionPath, entryPath));

                    // Ensure the resulting path is still inside our extraction directory
                    if (!fullDestPath.StartsWith(Path.GetFullPath(extractionPath), StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogMessage($"  WARNING: Skipping entry '{fileData.FileName}' - potential path traversal (Zip Slip) detected.");
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(fullDestPath) ?? throw new InvalidOperationException("fullDestPath cannot be null"));
                    using var fs = new FileStream(fullDestPath, FileMode.Create, FileAccess.Write);
                    extractor.ExtractFile(fileData.Index, fs);
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
        catch (SevenZipLibraryException ex)
        {
            var errorMessage = $"Error extracting {archiveFileName}: Could not load the 7-Zip x64 library.\n" +
                               "Please ensure 7z_x64.dll is in the application folder.\n" +
                               $"Exception: {ex.Message}";
            _logger.LogMessage($"  {errorMessage}");
            throw; // Re-throw the exception for the caller to handle UI
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error extracting {archiveFileName}: {ex.Message}");

            // Filter out common archive errors (corruption, wrong password, etc.) from bug reports
            if (ex is not (SevenZipArchiveException or ExtractionFailedException or FileNotFoundException) &&
                !ex.Message.Contains("Data error", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Invalid archive", StringComparison.OrdinalIgnoreCase))
            {
                _ = _bugReportService.SendBugReportAsync($"Error extracting {archiveFileName}. Exception: {ex}");
            }

            throw; // Re-throw the exception for the caller to handle UI
        }
    }

    public async Task<long> GetUncompressedArchiveSizeAsync(string archivePath, CancellationToken token)
    {
        try
        {
            return await Task.Run(() =>
            {
                _logger.LogMessage($"  Calculating uncompressed size for: {Path.GetFileName(archivePath)}");

                using var extractor = new SevenZipExtractor(archivePath);
                return extractor.ArchiveFileData.Sum(static x => (long)x.Size);
            }, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error getting uncompressed size of archive {Path.GetFileName(archivePath)}: {ex.Message}");
            _ = _bugReportService.SendBugReportAsync($"Error getting uncompressed archive size: {archivePath}. Exception: {ex}");
            throw; // Re-throw the exception for the caller to handle
        }
    }
}