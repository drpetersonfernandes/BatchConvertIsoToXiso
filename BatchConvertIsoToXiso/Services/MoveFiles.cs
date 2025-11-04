using System.IO;

namespace BatchConvertIsoToXiso.Services;

public interface IFileMover
{
    Task MoveTestedFileAsync(string sourceFile, string destinationFolder, string moveReason, CancellationToken token);
}

public class FileMoverService : IFileMover
{
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;

    public FileMoverService(ILogger logger, IBugReportService bugReportService)
    {
        _logger = logger;
        _bugReportService = bugReportService;
    }

    public async Task MoveTestedFileAsync(string sourceFile, string destinationFolder, string moveReason, CancellationToken token)
    {
        var fileName = Path.GetFileName(sourceFile);
        var destinationFile = Path.Combine(destinationFolder, fileName);

        try
        {
            token.ThrowIfCancellationRequested();

            if (!await Task.Run(() => Directory.Exists(destinationFolder), token))
            {
                await Task.Run(() => Directory.CreateDirectory(destinationFolder), token);
            }

            token.ThrowIfCancellationRequested();

            if (await Task.Run(() => File.Exists(destinationFile), token))
            {
                _logger.LogMessage($"  Cannot move {fileName}: Destination file already exists at {destinationFile}. Skipping move.");
                return;
            }

            if (!await Task.Run(() => File.Exists(sourceFile), token))
            {
                _logger.LogMessage($"  Cannot move {fileName}: Source file no longer exists. It may have already been moved.");
                return;
            }

            token.ThrowIfCancellationRequested();

            await Task.Run(() => File.Move(sourceFile, destinationFile), token);
            _logger.LogMessage($"  Moved {fileName} ({moveReason}) to {destinationFolder}");
        }
        catch (OperationCanceledException)
        {
            _logger.LogMessage($"  Move operation for {fileName} cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"  Error moving {fileName} to {destinationFolder}: {ex.Message}");
            _ = _bugReportService.SendBugReportAsync($"Error moving tested file {fileName}. Exception: {ex}");
        }
    }
}
