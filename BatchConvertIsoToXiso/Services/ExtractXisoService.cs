using System.Diagnostics;
using System.IO;
using BatchConvertIsoToXiso.interfaces;

namespace BatchConvertIsoToXiso.Services;

/// <summary>
/// Service for converting ISO files to XISO format using extract-xiso.exe
/// </summary>
public class ExtractXisoService : IExtractXisoService
{
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly IDiskMonitorService _diskMonitorService;
    private readonly string _extractXisoPath;

    public ExtractXisoService(ILogger logger, IBugReportService bugReportService, IDiskMonitorService diskMonitorService)
    {
        _logger = logger;
        _bugReportService = bugReportService;
        _diskMonitorService = diskMonitorService;
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _extractXisoPath = Path.Combine(appDir, "extract-xiso.exe");
    }

    public async Task<bool> ConvertIsoToXisoAsync(string inputFile, string outputFolder, bool skipSystemUpdate, CancellationToken token)
    {
        var fileName = Path.GetFileName(inputFile);
        _logger.LogMessage($"Converting '{fileName}' using extract-xiso.exe...");

        // Check if extract-xiso.exe exists
        if (!File.Exists(_extractXisoPath))
        {
            _logger.LogMessage($"[ERROR] extract-xiso.exe not found at: {_extractXisoPath}");
            return false;
        }

        // Create a temporary working directory for simplified filenames
        // extract-xiso has issues with spaces and special characters in paths
        var inputFileSize = new FileInfo(inputFile).Length;
        var tempWorkDir = ResolveTempDirectory(inputFileSize, "BatchConvertIsoToXiso_Work");

        try
        {
            Directory.CreateDirectory(tempWorkDir);

            // Create simplified filenames without spaces/special characters
            const string simpleInputName = "input.iso";
            const string simpleOutputName = "output.iso";
            var tempInputFile = Path.Combine(tempWorkDir, simpleInputName);
            var tempOutputFile = Path.Combine(tempWorkDir, simpleOutputName);

            // Copy input file to temp location with simple name
            _logger.LogMessage("  Preparing file for conversion...");
            await CopyFileWithProgressAsync(inputFile, tempInputFile, token);

            // Create output filename with .iso extension
            var outputFileName = Path.GetFileNameWithoutExtension(inputFile) + ".iso";

            var outputPath = Path.Combine(outputFolder, outputFileName);

            // Ensure output directory exists
            Directory.CreateDirectory(outputFolder);

            // Record start time before starting the process to detect newly created/modified files
            // Subtract 2 seconds to account for file system timestamp granularity (FAT32 has 2-second resolution)
            var conversionStartTime = DateTime.UtcNow.AddSeconds(-2);

            // Build arguments for extract-xiso using simplified paths
            // -r = rewrite (convert to XISO)
            // -d = output directory (temp work dir for simplicity)
            // -s = skip $SystemUpdate folder
            var arguments = $"-r -d \"{tempWorkDir}\"{(skipSystemUpdate ? " -s" : "")} \"{tempInputFile}\"";

            // Use manual disposal instead of 'using' to avoid capturing a disposed variable in the cancellation callback
            Process? process = null;
            CancellationTokenRegistration cancellationRegistration = default;
            try
            {
                process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = _extractXisoPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogMessage($"  [extract-xiso] {e.Data}");
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogMessage($"  [extract-xiso] ERROR: {e.Data}");
                    }
                };

                process.Start();
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Register cancellation handler before awaiting
                cancellationRegistration = token.Register(state =>
                {
                    var p = (Process?)state;
                    if (p != null)
                    {
                        // Offload to background thread to avoid blocking UI thread
                        _ = Task.Run(() => ProcessTerminatorHelper.TerminateProcess(p, "extract-xiso", _logger), token);
                    }
                }, process);

                await process.WaitForExitAsync(token);

                if (process.ExitCode == 0)
                {
                    // Check if output file was created in temp directory
                    if (File.Exists(tempOutputFile))
                    {
                        // Move the converted file to the final destination
                        _logger.LogMessage("  Moving converted file to output folder...");
                        File.Move(tempOutputFile, outputPath, true);
                        _logger.LogMessage($"Successfully converted '{fileName}' to XISO format.");
                        return true;
                    }

                    // Check for alternative output filename in temp directory
                    // Only consider files created/modified after this conversion started to avoid
                    // matching stale files from previous runs
                    var possibleOutputs = Directory.GetFiles(tempWorkDir, "*.iso")
                        .Where(f =>
                        {
                            try
                            {
                                var fileInfo = new FileInfo(f);
                                // File must be created or modified after this conversion started
                                return fileInfo.LastWriteTimeUtc >= conversionStartTime || fileInfo.CreationTimeUtc >= conversionStartTime;
                            }
                            catch
                            {
                                return false;
                            }
                        })
                        .ToList();

                    if (possibleOutputs.Count > 0)
                    {
                        // Move the converted file to the final destination
                        _logger.LogMessage("  Moving converted file to output folder...");
                        File.Move(possibleOutputs[0], outputPath, true);
                        _logger.LogMessage($"Successfully converted '{fileName}' to XISO format (found: {Path.GetFileName(possibleOutputs[0])}).");
                        return true;
                    }

                    _logger.LogMessage($"[WARNING] Conversion completed but output file not found for '{fileName}'.");
                    return false;
                }

                _logger.LogMessage($"[ERROR] extract-xiso.exe exited with code {process.ExitCode} for '{fileName}'.");
                return false;
            }
            finally
            {
                // Dispose registration before process to avoid accessing disposed process in callback
                cancellationRegistration.Dispose();
                process?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogMessage($"Conversion of '{fileName}' was canceled.");
            throw;
        }
        catch (Exception ex) when (IsDiskSpaceError(ex))
        {
            _logger.LogMessage($"[ERROR] Not enough disk space to convert '{fileName}': {ex.Message}");
            throw;
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            _logger.LogMessage($"[ERROR] Network error while converting '{fileName}': {ex.Message}\n\n" +
                "Please try:\n" +
                "1. Check that the network drive is still connected and accessible\n" +
                "2. Copy the file to a local drive before processing\n" +
                "3. Check your network connection stability");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"[ERROR] Failed to convert '{fileName}': {ex.Message}");
            _ = _bugReportService.SendBugReportAsync($"Failed to convert '{fileName}'", ex);
            return false;
        }
        finally
        {
            // Clean up temp working directory
            if (Directory.Exists(tempWorkDir))
            {
                try
                {
                    Directory.Delete(tempWorkDir, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    /// <summary>
    /// Copies a file with progress reporting and retry logic for transient IO errors
    /// </summary>
    private static async Task CopyFileWithProgressAsync(string sourcePath, string destPath, CancellationToken token)
    {
        const int bufferSize = 81920; // 80KB buffer
        const int maxRetries = 3;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous);
                await using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous);

                var buffer = new byte[bufferSize];
                int bytesRead;

                while ((bytesRead = await sourceStream.ReadAsync(buffer, token)) > 0)
                {
                    await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
                }

                return; // Success
            }
            catch (IOException) when (attempt < maxRetries)
            {
                // Clean up partial dest file before retry
                try
                {
                    if (File.Exists(destPath)) File.Delete(destPath);
                }
                catch
                {
                    // ignored
                }

                await Task.Delay(attempt * 2000, token);
            }
        }
    }

    private string ResolveTempDirectory(long requiredSize, string tempSubfolder)
    {
        var defaultTempPath = Path.GetTempPath();
        var defaultTempDriveRoot = Path.GetPathRoot(defaultTempPath);
        var requiredWithBuffer = requiredSize + Math.Max(requiredSize / 10, 200L * 1024 * 1024);

        if (defaultTempDriveRoot != null)
        {
            try
            {
                var defaultDrive = new DriveInfo(defaultTempDriveRoot);
                if (defaultDrive.IsReady && defaultDrive.AvailableFreeSpace >= requiredWithBuffer)
                    return Path.Combine(defaultTempPath, tempSubfolder, Guid.NewGuid().ToString());
            }
            catch
            {
                // Ignore and fall through to alternative search
            }
        }

        var altDrive = _diskMonitorService.FindDriveWithFreeSpace(requiredSize, defaultTempDriveRoot);
        if (altDrive != null)
        {
            _logger.LogMessage($"  Default temp drive has insufficient space. Using alternative drive: {altDrive}");
            return Path.Combine(altDrive, tempSubfolder, Guid.NewGuid().ToString());
        }

        var requiredFormatted = Formatter.FormatBytes(requiredWithBuffer);
        var defaultAvailable = Formatter.FormatBytes(_diskMonitorService.GetAvailableFreeSpace(defaultTempPath));
        throw new IOException($"Not enough disk space to create temporary files. Required: {requiredFormatted}, Available: {defaultAvailable}. No other local drives have sufficient free space.");
    }

    private static bool IsDiskSpaceError(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            var hResult = Math.Abs(ioEx.HResult) & 0xFFFF;
            if (hResult is 0x70 or 0x27) return true; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        }

        if (ex.InnerException is IOException innerIoEx)
        {
            var hResult = Math.Abs(innerIoEx.HResult) & 0xFFFF;
            if (hResult is 0x70 or 0x27) return true;
        }

        var message = ex.Message;
        if (ex.InnerException != null)
        {
            message += " " + ex.InnerException.Message;
        }

        return message.Contains("Not enough space", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not enough disk space", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("insufficient disk space", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Disk full", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Espace insuffisant", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("disque plein", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines if an exception is related to network connectivity issues.
    /// Supports error messages in multiple languages (English, German, French, Spanish, Italian).
    /// </summary>
    private static bool IsNetworkError(Exception ex)
    {
        if (ex is not IOException ioEx)
            return false;

        var message = ioEx.Message;

        // English
        if (message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("device", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no longer available", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // German
        if (message.Contains("Netzwerk", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("nicht mehr verfügbar", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // French
        if (message.Contains("réseau", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("n'est plus disponible", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Spanish
        if (message.Contains("red", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Italian
        if (message.Contains("rete", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
