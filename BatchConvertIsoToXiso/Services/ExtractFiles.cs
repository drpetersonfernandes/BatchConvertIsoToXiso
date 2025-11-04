using System.IO;
using SevenZip;

namespace BatchConvertIsoToXiso.Services;

public interface IFileExtractor
{
    Task<bool> ExtractArchiveAsync(string archivePath, string extractionPath, CancellationTokenSource cts);
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
        catch (Exception ex)
        {
            _logger.LogMessage($"Error extracting {archiveFileName}: {ex.Message}");
            _ = _bugReportService.SendBugReportAsync($"Error extracting {archiveFileName}. Exception: {ex}");
            return false;
        }
    }
}
