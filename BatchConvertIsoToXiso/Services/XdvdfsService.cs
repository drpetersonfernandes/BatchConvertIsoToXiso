using System.Diagnostics;
using System.IO;
using BatchConvertIsoToXiso.interfaces;

namespace BatchConvertIsoToXiso.Services;

/// <summary>
/// Service for converting ISO files to XISO format using xdvdfs.exe
/// </summary>
public class XdvdfsService : IXdvdfsService
{
    private readonly ILogger _logger;
    private readonly string _xdvdfsPath;

    public XdvdfsService(ILogger logger)
    {
        _logger = logger;
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _xdvdfsPath = Path.Combine(appDir, "xdvdfs.exe");
    }

    public async Task<bool> ConvertIsoToXisoAsync(string inputFile, string outputFolder, CancellationToken token)
    {
        var fileName = Path.GetFileName(inputFile);
        _logger.LogMessage($"Converting '{fileName}' using xdvdfs.exe...");
        _logger.LogMessage("[WARNING] The 'Skip $SystemUpdate' feature is NOT supported by the xdvdfs tool.");

        // Check if xdvdfs.exe exists
        if (!File.Exists(_xdvdfsPath))
        {
            _logger.LogMessage($"[ERROR] xdvdfs.exe not found at: {_xdvdfsPath}");
            return false;
        }

        // Create output filename with .iso extension
        var outputFileName = Path.GetFileNameWithoutExtension(inputFile) + ".iso";
        var outputPath = Path.Combine(outputFolder, outputFileName);

        // Ensure output directory exists
        Directory.CreateDirectory(outputFolder);

        // Repacking an Image
        // Images can be repacked from an existing ISO image:
        // xdvdfs pack <input-image> [optional output path]
        // This will create an iso that matches 1-to-1 with the input image.

        var arguments = $"pack \"{inputFile}\" \"{outputPath}\"";

        // Use manual disposal instead of 'using' to avoid capturing a disposed variable in the cancellation callback
        Process? process = null;
        CancellationTokenRegistration cancellationRegistration = default;
        try
        {
            process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _xdvdfsPath,
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
                    _logger.LogMessage($"  [xdvdfs] {e.Data}");
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogMessage($"  [xdvdfs] ERROR: {e.Data}");
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
                    _ = Task.Run(() => ProcessTerminatorHelper.TerminateProcess(p, "xdvdfs", _logger), token);
                }
            }, process);

            await process.WaitForExitAsync(token);

            if (process.ExitCode == 0)
            {
                if (File.Exists(outputPath))
                {
                    _logger.LogMessage($"Successfully converted '{fileName}' to XISO format using xdvdfs.");
                    return true;
                }

                _logger.LogMessage($"[WARNING] xdvdfs completed but output file not found for '{fileName}'.");
                return false;
            }

            _logger.LogMessage($"[ERROR] xdvdfs.exe exited with code {process.ExitCode} for '{fileName}'.");
            return false;
        }
        finally
        {
            // Dispose registration before process to avoid accessing disposed process in callback
            cancellationRegistration.Dispose();
            process?.Dispose();
        }
    }
}
