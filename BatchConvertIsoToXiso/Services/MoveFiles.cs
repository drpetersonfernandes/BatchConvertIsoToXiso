using System.IO;
using BatchConvertIsoToXiso.interfaces;

namespace BatchConvertIsoToXiso.Services;

public class FileMoverService : IFileMover
{
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly IDiskMonitorService _diskMonitorService;

    // Maximum retry attempts for network operations
    private const int MaxRetryAttempts = 5;

    // Initial delay in milliseconds (will be used for exponential backoff)
    private const int InitialRetryDelayMs = 500;

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

            // Check if either source or destination is a network path
            var isNetworkOperation = PathHelper.IsNetworkPath(sourceFile) || PathHelper.IsNetworkPath(destinationFolder);

            if (isNetworkOperation)
            {
                // Use retry logic for network operations
                await MoveFileWithNetworkRetryAsync(sourceFile, destinationFile, fileName, token);
            }
            else
            {
                // Local operation - no retry needed
                await Task.Run(() => File.Move(sourceFile, destinationFile), token);
            }

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
    /// Moves a file with retry logic and exponential backoff for network transient errors.
    /// </summary>
    private async Task MoveFileWithNetworkRetryAsync(string source, string dest, string fileName, CancellationToken token)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt < MaxRetryAttempts; attempt++)
        {
            try
            {
                await Task.Run(() => File.Move(source, dest), token);
                return; // Success
            }
            catch (IOException ex) when (PathHelper.IsNetworkError(ex))
            {
                lastException = ex;

                if (attempt < MaxRetryAttempts - 1)
                {
                    // Exponential backoff: 500ms, 1000ms, 2000ms, 4000ms, etc.
                    var delayMs = InitialRetryDelayMs * (int)Math.Pow(2, attempt);
                    _logger.LogMessage($"  Network error moving {fileName}, retrying in {delayMs}ms... (attempt {attempt + 1}/{MaxRetryAttempts})");
                    await Task.Delay(delayMs, token);
                }
            }
        }

        // All retries exhausted
        if (lastException != null)
        {
            throw new IOException($"Failed to move file after {MaxRetryAttempts} attempts due to network errors. Last error: {lastException.Message}", lastException);
        }
    }

    /// <summary>
    /// Moves a file with retry logic to handle transient locks and network errors.
    /// </summary>
    public async Task RobustMoveFileAsync(string source, string dest, CancellationToken token)
    {
        var isNetworkOperation = PathHelper.IsNetworkPath(source) || PathHelper.IsNetworkPath(dest);
        var maxAttempts = isNetworkOperation ? MaxRetryAttempts : 3;
        Exception? lastException = null;

        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                await Task.Run(() => File.Move(source, dest, true), token);
                return;
            }
            catch (IOException ex) when (isNetworkOperation && PathHelper.IsNetworkError(ex) && i < maxAttempts - 1)
            {
                lastException = ex;
                // Exponential backoff for network errors
                var delayMs = InitialRetryDelayMs * (int)Math.Pow(2, i);
                await Task.Delay(delayMs, token);
            }
            catch when (i < maxAttempts - 1)
            {
                // For non-network errors, use fixed 500ms delay
                await Task.Delay(500, token);
            }
        }

        // Final attempt without catch
        if (lastException != null)
        {
            throw new IOException($"Failed to move file after {maxAttempts} attempts. Last error: {lastException.Message}", lastException);
        }

        await Task.Run(() => File.Move(source, dest, true), token);
    }
}