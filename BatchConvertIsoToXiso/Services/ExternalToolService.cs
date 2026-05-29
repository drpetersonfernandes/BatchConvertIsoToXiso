using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using BatchConvertIsoToXiso.interfaces;

namespace BatchConvertIsoToXiso.Services;

public partial class ExternalToolService : IExternalToolService
{
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly string _bchunkPath;

    public ExternalToolService(ILogger logger, IBugReportService bugReportService)
    {
        _logger = logger;
        _bugReportService = bugReportService;
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _bchunkPath = Path.Combine(appDir, "bchunk.exe");
    }

    public async Task<string?> ConvertCueBinToIsoAsync(string cuePath, string tempOutputDir, CancellationToken token)
    {
        var cueFileName = Path.GetFileName(cuePath);
        _logger.LogMessage($"Converting CUE/BIN to ISO: '{cueFileName}'...");

        var binPath = await ParseCueForBinFileAsync(cuePath, token);
        if (string.IsNullOrEmpty(binPath))
        {
            _logger.LogMessage($"[ERROR] Could not find BIN file for CUE: '{cueFileName}'");
            return null;
        }

        var binFileName = Path.GetFileName(binPath);
        _logger.LogMessage($"  Found BIN file: '{binFileName}'");

        var outputBaseName = Path.GetFileNameWithoutExtension(cuePath);
        var result = await RunProcessAsync(_bchunkPath, $"\"{binPath}\" \"{cuePath}\" \"{outputBaseName}\"", tempOutputDir, outputBaseName, token);

        if (result != 0)
        {
            _logger.LogMessage($"[ERROR] Failed to convert CUE/BIN to ISO for '{cueFileName}'. bchunk.exe exited with code {result}.");
            return null;
        }

        var isoFile = Directory.GetFiles(tempOutputDir, "*.iso").FirstOrDefault();
        if (isoFile != null)
        {
            _logger.LogMessage($"  Successfully converted CUE/BIN to ISO: '{Path.GetFileName(isoFile)}'");
        }
        else
        {
            _logger.LogMessage($"[WARNING] CUE/BIN conversion completed but no ISO file was found for '{cueFileName}'.");
        }

        return isoFile;
    }

    private async Task<int?> RunProcessAsync(string fileName, string arguments, string? workingDir, string contextName, CancellationToken token)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? AppDomain.CurrentDomain.BaseDirectory
            };

            process.Start();

            await using (token.Register(state =>
                         {
                             var p = (Process?)state;
                             if (p != null)
                             {
                                 // Offload to background thread to avoid blocking UI thread
                                 _ = Task.Run(() => ProcessTerminatorHelper.TerminateProcess(p, contextName, _logger), token);
                             }
                         }, process))
            {
                await process.WaitForExitAsync(token);
            }

            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Process execution failed ({contextName}): {ex.Message}");
            _ = _bugReportService.SendBugReportAsync($"Process execution failed ({contextName})", ex);
            return null;
        }
    }

    private static async Task<string?> ParseCueForBinFileAsync(string cuePath, CancellationToken token)
    {
        var cueDir = Path.GetDirectoryName(cuePath);
        if (cueDir == null) return null;

        try
        {
            var lines = await File.ReadAllLinesAsync(cuePath, token);
            foreach (var line in lines)
            {
                var match = CueRegex().Match(line.Trim());
                if (!match.Success) continue;

                var rawBinName = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;

                // Combine with CUE directory while preserving relative paths (e.g. "data\file.bin")
                var binPath = Path.Combine(cueDir, rawBinName);
                if (File.Exists(binPath)) return binPath;
            }
        }
        catch
        {
            /* ignore */
        }

        var fallback = Path.ChangeExtension(cuePath, ".bin");
        return File.Exists(fallback) ? fallback : null;
    }

    [GeneratedRegex("""^FILE\s+(?:"(.+)"|(\S+))\s+\S+""", RegexOptions.IgnoreCase)]
    private static partial Regex CueRegex();
}
