using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private async Task<string?> ConvertCueBinToIsoAsync(string cuePath, string tempOutputDir)
    {
        var bchunkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bchunk.exe");
        if (!File.Exists(bchunkPath))
        {
            _logger.LogMessage("  Error: bchunk.exe not found. Cannot convert CUE/BIN files.");
            _ = ReportBugAsync("bchunk.exe not found for CUE/BIN conversion.");
            return null;
        }

        var binPath = await ParseCueForBinFileAsync(cuePath);
        if (string.IsNullOrEmpty(binPath))
        {
            _logger.LogMessage($"  Could not find the corresponding BIN file for {Path.GetFileName(cuePath)}.");
            return null;
        }

        var outputBaseName = Path.GetFileNameWithoutExtension(cuePath);
        var arguments = $"\"{binPath}\" \"{cuePath}\" \"{outputBaseName}\"";
        _logger.LogMessage($"  Running bchunk.exe {arguments}");

        var processOutputCollector = new StringBuilder();
        Process? processRef;
        CancellationTokenRegistration cancellationRegistration = default;

        try
        {
            await Task.Run(() => Directory.CreateDirectory(tempOutputDir), _cts.Token);

            var createdIso = await Task.Run(() => Directory.GetFiles(tempOutputDir, "*.iso").FirstOrDefault(), _cts.Token);
            if (createdIso == null)
            {
                _logger.LogMessage("  bchunk.exe finished, but no ISO file was found in the output directory.");
                return null;
            }

            using var process = new Process();
            processRef = process;
            process.StartInfo = new ProcessStartInfo
            {
                FileName = bchunkPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempOutputDir
            };

            cancellationRegistration = _cts.Token.Register(() =>
            {
                try
                {
                    if (processRef != null && !processRef.HasExited)
                    {
                        processRef.Kill(true);
                    }
                }
                catch
                {
                    // Ignore
                }
            });
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data != null)
                    lock (processOutputCollector)
                    {
                        processOutputCollector.AppendLine(args.Data);
                    }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                    lock (processOutputCollector)
                    {
                        processOutputCollector.AppendLine(CultureInfo.InvariantCulture, $"STDERR: {args.Data}");
                    }
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(_cts.Token);
            _cts.Token.ThrowIfCancellationRequested();
            var collectedOutput = processOutputCollector.ToString();
            _logger.LogMessage($"  Output from bchunk.exe for '{Path.GetFileName(cuePath)}':\n{collectedOutput}");
            if (process.ExitCode != 0)
            {
                _logger.LogMessage($"  bchunk.exe failed with exit code {process.ExitCode}.");
                return null;
            }

            _logger.LogMessage($"  Successfully created temporary ISO: {Path.GetFileName(createdIso)}");
            return createdIso;
        }
        catch (OperationCanceledException)
        {
            _logger.LogMessage($"  CUE/BIN conversion for {Path.GetFileName(cuePath)} was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"  Error running bchunk.exe for {Path.GetFileName(cuePath)}: {ex.Message}");
            _ = ReportBugAsync($"Exception during bchunk.exe for {Path.GetFileName(cuePath)}", ex);
            return null;
        }
        finally
        {
            await cancellationRegistration.DisposeAsync();
        }
    }

    private async Task DeleteCueAndBinFilesAsync(string cuePath)
    {
        _logger.LogMessage($"  Deleting original CUE/BIN files for {Path.GetFileName(cuePath)}...");
        var binPath = await ParseCueForBinFileAsync(cuePath);
        await TryDeleteFileAsync(cuePath);
        if (!string.IsNullOrEmpty(binPath)) await TryDeleteFileAsync(binPath);
    }

    private async Task<string?> ParseCueForBinFileAsync(string cuePath)
    {
        var cueDir = Path.GetDirectoryName(cuePath);
        if (cueDir == null) return null;

        try
        {
            var lines = await File.ReadAllLinesAsync(cuePath, _cts.Token);
            foreach (var line in lines)
            {
                if (!line.Trim().StartsWith("FILE", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = line.Split('"');
                if (parts.Length < 2) continue;

                var binFileName = parts[1];
                var binPath = Path.Combine(cueDir, binFileName);
                if (await Task.Run(() => File.Exists(binPath), _cts.Token))
                {
                    return binPath;
                }

                _logger.LogMessage($"  BIN file '{binFileName}' specified in CUE not found at '{binPath}'.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"  Error reading CUE file '{cuePath}': {ex.Message}");
        }

        // Fallback: check for a .bin file with the same base name as the .cue file
        var fallbackBinPath = Path.ChangeExtension(cuePath, ".bin");
        if (await Task.Run(() => File.Exists(fallbackBinPath), _cts.Token))
        {
            _logger.LogMessage($"  Using fallback to find BIN file: {Path.GetFileName(fallbackBinPath)}");
            return fallbackBinPath;
        }

        _logger.LogMessage($"  Could not find a valid BIN file for CUE: {Path.GetFileName(cuePath)}");
        return null;
    }
}