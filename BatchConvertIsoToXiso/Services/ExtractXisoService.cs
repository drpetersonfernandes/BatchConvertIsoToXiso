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
    private readonly string _extractXisoPath;

    public ExtractXisoService(ILogger logger)
    {
        _logger = logger;
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _extractXisoPath = Path.Combine(appDir, "extract-xiso.exe");
    }

    public async Task<bool> ConvertIsoToXisoAsync(string inputFile, string outputFolder, CancellationToken token)
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
        var tempWorkDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_Work", Guid.NewGuid().ToString());

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
            var arguments = $"-r -d \"{tempWorkDir}\" \"{tempInputFile}\"";

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
        catch (Exception ex)
        {
            _logger.LogMessage($"[ERROR] Failed to convert '{fileName}': {ex.Message}");
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
    /// Copies a file with progress reporting (simplified, no progress percentage for now)
    /// </summary>
    private static async Task CopyFileWithProgressAsync(string sourcePath, string destPath, CancellationToken token)
    {
        const int bufferSize = 81920; // 80KB buffer
        await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous);
        await using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous);

        var buffer = new byte[bufferSize];
        int bytesRead;

        while ((bytesRead = await sourceStream.ReadAsync(buffer, token)) > 0)
        {
            await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
        }
    }
}
