using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace BatchConvertIsoToXiso.Services;

public interface IExternalToolService
{
    Task<string?> ConvertCueBinToIsoAsync(string cuePath, string tempOutputDir, CancellationToken token);
}

public partial class ExternalToolService : IExternalToolService
{
    private readonly ILogger _logger;
    private readonly string _bchunkPath;

    public ExternalToolService(ILogger logger)
    {
        _logger = logger;
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _bchunkPath = Path.Combine(appDir, "bchunk.exe");
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
            // Use a standard using block to make the process scope explicit
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

            // ReSharper disable once AccessToDisposedClosure
            await using (token.Register(() => ProcessTerminatorHelper.TerminateProcess(process, contextName, _logger)))
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