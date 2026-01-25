using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using BatchConvertIsoToXiso.Models;

namespace BatchConvertIsoToXiso.Services;

public interface IExternalToolService
{
    Task<ConversionToolResultStatus> RunConversionAsync(string inputFile, bool skipSystemUpdate, CancellationToken token);
    Task<string?> ConvertCueBinToIsoAsync(string cuePath, string tempOutputDir, CancellationToken token);
    Task<bool> RunIsoExtractionAsync(string inputFile, string tempExtractionDir, CancellationToken token);
}

public partial class ExternalToolService : IExternalToolService
{
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly string _extractXisoPath;
    private readonly string _bchunkPath;

    public ExternalToolService(ILogger logger, IBugReportService bugReportService)
    {
        _logger = logger;
        _bugReportService = bugReportService;
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _extractXisoPath = Path.Combine(appDir, "extract-xiso.exe");
        _bchunkPath = Path.Combine(appDir, "bchunk.exe");
    }

    public async Task<ConversionToolResultStatus> RunConversionAsync(string inputFile, bool skipSystemUpdate, CancellationToken token)
    {
        var originalFileName = Path.GetFileName(inputFile);
        var arguments = skipSystemUpdate ? $"-s -r \"{inputFile}\"" : $"-r \"{inputFile}\"";

        var outputLines = new List<string>();
        var result = await RunProcessAsync(_extractXisoPath, arguments, Path.GetDirectoryName(inputFile), outputLines, originalFileName, token);

        if (result == null) return ConversionToolResultStatus.Failed; // Canceled or critical start error

        var outputString = string.Join(Environment.NewLine, outputLines);

        if (result is 0 or 1)
        {
            if (outputLines.Any(static l => l.Contains("is already optimized", StringComparison.OrdinalIgnoreCase) ||
                                            l.Contains("already an XISO image", StringComparison.OrdinalIgnoreCase)))
                return ConversionToolResultStatus.Skipped;

            if (outputLines.Any(static l => l.Contains("successfully rewritten", StringComparison.OrdinalIgnoreCase)) || result == 0)
                return ConversionToolResultStatus.Success;
        }

        // Handle known validation errors
        if (outputString.Contains("does not appear to be a valid xbox iso image", StringComparison.OrdinalIgnoreCase) ||
            outputString.Contains("failed to rewrite xbox iso image", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogMessage($"Tool reported invalid ISO for {originalFileName}.");
            return ConversionToolResultStatus.Failed;
        }

        _ = _bugReportService.SendBugReportAsync($"extract-xiso -r failed for {originalFileName} (Exit: {result}). Output: {outputString}");
        return ConversionToolResultStatus.Failed;
    }

    public async Task<bool> RunIsoExtractionAsync(string inputFile, string tempExtractionDir, CancellationToken token)
    {
        var isoFileName = Path.GetFileName(inputFile);
        var outputLines = new List<string>();
        var result = await RunProcessAsync(_extractXisoPath, $"-x \"{inputFile}\"", tempExtractionDir, outputLines, isoFileName, token);

        if (result == null) return false;

        // Check if anything was extracted into the temp directory (usually a subfolder named after the XBE title)
        var filesExtracted = Directory.Exists(tempExtractionDir) && Directory.EnumerateFileSystemEntries(tempExtractionDir).Any();

        switch (result)
        {
            case 0 when filesExtracted:
                return true;
            case 1 when filesExtracted:
            {
                // Check if errors are just benign metadata issues
                var hasCriticalError = outputLines.Any(static l => l.StartsWith("STDERR:", StringComparison.Ordinal) && !l.Contains("No such file or directory"));
                return !hasCriticalError;
            }
            default:
                return false;
        }
    }

    public async Task<string?> ConvertCueBinToIsoAsync(string cuePath, string tempOutputDir, CancellationToken token)
    {
        var binPath = await ParseCueForBinFileAsync(cuePath, token);
        if (string.IsNullOrEmpty(binPath)) return null;

        var outputBaseName = Path.GetFileNameWithoutExtension(cuePath);
        var outputLines = new List<string>();
        var result = await RunProcessAsync(_bchunkPath, $"\"{binPath}\" \"{cuePath}\" \"{outputBaseName}\"", tempOutputDir, outputLines, outputBaseName, token);

        if (result != 0) return null;

        return Directory.GetFiles(tempOutputDir, "*.iso").FirstOrDefault();
    }

    private async Task<int?> RunProcessAsync(string fileName, string arguments, string? workingDir, List<string> outputStore, string contextName, CancellationToken token)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? AppDomain.CurrentDomain.BaseDirectory
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    lock (outputStore)
                    {
                        outputStore.Add(e.Data);
                    }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    lock (outputStore)
                    {
                        outputStore.Add($"STDERR: {e.Data}");
                    }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var registration = token.Register(() => ProcessTerminatorHelper.TerminateProcess(process, contextName, _logger));
            try
            {
                await process.WaitForExitAsync(token);
            }
            finally
            {
                await registration.DisposeAsync();
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

                var binFileName = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
                var binPath = Path.Combine(cueDir, binFileName);
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