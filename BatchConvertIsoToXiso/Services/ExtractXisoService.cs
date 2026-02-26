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

        try
        {
            // Create output filename (replace .iso with .xiso.iso or add .xiso)
            var outputFileName = Path.GetFileNameWithoutExtension(inputFile);
            if (!outputFileName.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase))
            {
                outputFileName += ".xiso.iso";
            }
            else
            {
                outputFileName += ".iso";
            }

            var outputPath = Path.Combine(outputFolder, outputFileName);

            // Ensure output directory exists
            Directory.CreateDirectory(outputFolder);

            // Record start time before starting the process to detect newly created/modified files
            var conversionStartTime = DateTime.UtcNow;

            // Build arguments for extract-xiso
            // -r = rewrite (convert to XISO)
            // -o = output directory
            // -s = skip $SystemUpdate folder
            var arguments = $"-r -o \"{outputFolder}\" \"{inputFile}\"";

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
                    // Check if output file was created
                    if (File.Exists(outputPath))
                    {
                        _logger.LogMessage($"Successfully converted '{fileName}' to XISO format.");
                        return true;
                    }

                    // Check for alternative output filename (extract-xiso might use different naming)
                    // Only consider files created/modified after this conversion started to avoid
                    // matching stale files from previous runs
                    var possibleOutputs = Directory.GetFiles(outputFolder, "*.iso")
                        .Where(f =>
                        {
                            var fileNameMatch = Path.GetFileName(f).Contains(Path.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase);
                            if (!fileNameMatch) return false;

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
    }
}
