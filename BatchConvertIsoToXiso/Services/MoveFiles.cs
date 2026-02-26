using System.IO;
using BatchConvertIsoToXiso.interfaces;

namespace BatchConvertIsoToXiso.Services;

public class FileMoverService : IFileMover
{
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly IDiskMonitorService _diskMonitorService;

    public FileMoverService(ILogger logger, IBugReportService bugReportService, IDiskMonitorService diskMonitorService)
    {
        _logger = logger;
        _bugReportService = bugReportService;
        _diskMonitorService = diskMonitorService;
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

            // Check available disk space before moving
            var sourceFileInfo = new FileInfo(sourceFile);
            var availableSpace = _diskMonitorService.GetAvailableFreeSpace(destinationFolder);
            if (availableSpace > 0 && sourceFileInfo.Length > availableSpace)
            {
                var requiredSpace = Formatter.FormatBytes(sourceFileInfo.Length);
                var availableSpaceFormatted = Formatter.FormatBytes(availableSpace);
                _logger.LogMessage($"  Cannot move {fileName}: Insufficient disk space. Required: {requiredSpace}, Available: {availableSpaceFormatted}");
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

    /// <summary>
    /// Moves a file with retry logic to handle transient locks.
    /// </summary>
    public async Task RobustMoveFileAsync(string source, string dest, CancellationToken token)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                await Task.Run(() => File.Move(source, dest, true), token);
                return;
            }
            catch when (i < 2)
            {
                await Task.Delay(500, token);
            }
        }

        File.Move(source, dest, true);
    }
}