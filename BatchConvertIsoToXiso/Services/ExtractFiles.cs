using System.IO;
using SevenZip;

namespace BatchConvertIsoToXiso.Services;

public interface IFileExtractor
{
    Task<bool> ExtractArchiveAsync(string archivePath, string extractionPath, CancellationTokenSource cts);
    Task<long> GetUncompressedArchiveSizeAsync(string archivePath, CancellationToken token);
}

public class FileExtractorService : IFileExtractor
{
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;

    public FileExtractorService(ILogger logger, IBugReportService bugReportService)
    {
        _logger = logger;
        _bugReportService = bugReportService;
    }

    public async Task<bool> ExtractArchiveAsync(string archivePath, string extractionPath, CancellationTokenSource cts)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        _logger.LogMessage($"Extracting: {archiveFileName} using SevenZipExtractor to {extractionPath}");

        try
        {
            await Task.Run(() =>
            {
                using var extractor = new SevenZipExtractor(archivePath);
                extractor.ExtractArchive(extractionPath); // Extract all files
            }, cts.Token);

            _logger.LogMessage($"Successfully extracted: {archiveFileName}");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogMessage($"Extraction of {archiveFileName} was canceled.");
            throw;
        }
        catch (SevenZipLibraryException ex)
        {
            var errorMessage = $"Error extracting {archiveFileName}: Could not load the 7-Zip x64 library. " +
                               "Please ensure 7z_x64.dll is in the application folder.";
            _logger.LogMessage(errorMessage);
            _ = _bugReportService.SendBugReportAsync($"Error extracting {archiveFileName}. SevenZipLibraryException: {ex}");
            throw; // Re-throw the exception for the caller to handle UI
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error extracting {archiveFileName}: {ex.Message}");
            _ = _bugReportService.SendBugReportAsync($"Error extracting {archiveFileName}. Exception: {ex}");
            throw; // Re-throw the exception for the caller to handle UI
        }
    }

    public async Task<long> GetUncompressedArchiveSizeAsync(string archivePath, CancellationToken token)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var extractor = new SevenZipExtractor(archivePath);
                return extractor.ArchiveFileData.Sum(x => (long)x.Size);
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