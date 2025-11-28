using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BatchConvertIsoToXiso.Services;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private async Task<bool> RunIsoExtractionToTempAsync(string extractXisoPath, string inputFile, string tempExtractionDir)
    {
        var isoFileName = Path.GetFileName(inputFile);
        _logger.LogMessage($"    Detailed Extraction Attempt for: {isoFileName}");

        var processOutputCollector = new StringBuilder();
        Process? processRef;
        var extractionStarted = false;
        CancellationTokenRegistration cancellationRegistration = default;

        try
        {
            await Task.Run(() => Directory.CreateDirectory(tempExtractionDir), _cts.Token);

            using var process = new Process();
            processRef = process;
            process.StartInfo = new ProcessStartInfo
            {
                FileName = extractXisoPath,
                Arguments = $"-x \"{inputFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempExtractionDir
            };

            var isoNameWithoutExtension = Path.GetFileNameWithoutExtension(isoFileName);
            var expectedExtractionSubDir = Path.Combine(tempExtractionDir, isoNameWithoutExtension);

            cancellationRegistration = _cts.Token.Register(() =>
            {
                try
                {
                    if (processRef != null)
                    {
                        _logger.LogMessage($"    Cancellation requested for extract-xiso extraction of {isoFileName}.");
                        var success = ProcessTerminatorHelper.TerminateProcess(processRef, $"extract-xiso extraction ({isoFileName})", _logger);

                        if (!success)
                        {
                            _logger.LogMessage($"    WARNING: Failed to terminate extract-xiso extraction process for {isoFileName}. File locks may persist.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogMessage($"    Error during cancellation of extraction for {isoFileName}: {ex.Message}");
                }
            });

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null) return;

                lock (processOutputCollector)
                {
                    processOutputCollector.AppendLine(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrEmpty(args.Data)) return;

                lock (processOutputCollector)
                {
                    processOutputCollector.AppendLine(CultureInfo.InvariantCulture, $"STDERR: {args.Data}");
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            extractionStarted = true;

            await process.WaitForExitAsync(_cts.Token);
            _cts.Token.ThrowIfCancellationRequested();

            var collectedOutput = processOutputCollector.ToString();
            _logger.LogMessage($"    Process Exit Code for '{isoFileName}': {process.ExitCode}");
            _logger.LogMessage($"    Full Output Log from extract-xiso -x for '{isoFileName}':\n{collectedOutput}");


            var filesWereExtracted = false;
            if (Directory.Exists(expectedExtractionSubDir))
            {
                var extractedFiles = Directory.GetFiles(expectedExtractionSubDir, "*", SearchOption.AllDirectories);
                filesWereExtracted = extractedFiles.Length > 0;
                _logger.LogMessage($"    Files found by Directory.GetFiles in '{expectedExtractionSubDir}' (recursive): {extractedFiles.Length}. filesWereExtracted: {filesWereExtracted}");
            }
            else
            {
                _logger.LogMessage($"    Expected extraction subdirectory '{expectedExtractionSubDir}' not found.");
            }

            var summaryLinePresent = collectedOutput.Contains("files in") && collectedOutput.Contains("total") && collectedOutput.Contains("bytes");

            switch (process.ExitCode)
            {
                case 0 when !filesWereExtracted:
                    _logger.LogMessage("    extract-xiso process exited with 0, but no files were extracted. Considered a failure.");
                    return false;
                case 0:
                {
                    var criticalErrorInStdErr = collectedOutput.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                        .Any(static line => line.StartsWith("STDERR:", StringComparison.OrdinalIgnoreCase) &&
                                            (line.Contains("failed to extract", StringComparison.OrdinalIgnoreCase) ||
                                             line.Contains("error extracting", StringComparison.OrdinalIgnoreCase) ||
                                             line.Contains("cannot open", StringComparison.OrdinalIgnoreCase) ||
                                             line.Contains("not a valid", StringComparison.OrdinalIgnoreCase)));
                    if (criticalErrorInStdErr)
                    {
                        _logger.LogMessage("    extract-xiso process exited with 0 and files were extracted, but critical error messages were found in STDERR. Considered a failure.");
                        return false;
                    }

                    _logger.LogMessage("    extract-xiso process completed successfully (ExitCode 0, files extracted, no critical errors in log).");
                    return true;
                }
                case 1:
                {
                    var stdErrLines = collectedOutput.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(static line => line.StartsWith("STDERR:", StringComparison.OrdinalIgnoreCase))
                        .Select(static line => line.Substring("STDERR:".Length).Trim())
                        .ToArray();

                    var onlyKnownBenignErrors = stdErrLines.All(errLine =>
                        errLine.Contains("open error: -d No such file or directory") ||
                        errLine.Contains("open error: LoadScreen_BlackScreen.nif No such file or directory") ||
                        errLine.Contains("failed to extract xbox iso image")
                    );

                    if (filesWereExtracted && summaryLinePresent && (stdErrLines.Length == 0 || onlyKnownBenignErrors))
                    {
                        _logger.LogMessage("    extract-xiso process exited with 1, but files were extracted, summary line present, and STDERR contained only known benign issues (or was empty). Considered a pass for testing.");
                        return true;
                    }
                    else
                    {
                        _logger.LogMessage($"    extract-xiso process finished with exit code 1. Files extracted: {filesWereExtracted}. Summary line: {summaryLinePresent}. STDERR lines: {string.Join("; ", stdErrLines)}. Considered a failure.");
                        return false;
                    }
                }
                default:
                    _logger.LogMessage($"    extract-xiso process finished with non-zero exit code: {process.ExitCode}. Considered a failure.");
                    return false;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogMessage($"    Extraction for {isoFileName} was canceled.");

            // Clean up partial extraction
            if (extractionStarted && Directory.Exists(Path.Combine(tempExtractionDir, Path.GetFileNameWithoutExtension(isoFileName))))
            {
                var expectedExtractionSubDir = Path.Combine(tempExtractionDir, Path.GetFileNameWithoutExtension(isoFileName));
                try
                {
                    await Task.Delay(500, CancellationToken.None);

                    // Attempt to delete the partial extraction directory
                    if (Directory.Exists(expectedExtractionSubDir))
                    {
                        Directory.Delete(expectedExtractionSubDir, true);
                        _logger.LogMessage($"    Cleaned up partial extraction directory for {isoFileName}");
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogMessage($"    Warning: Could not clean up partial extraction: {cleanupEx.Message}");
                }
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"    Critical Error during extraction of {isoFileName}: {ex.Message}");
            _ = ReportBugAsync($"Critical error during RunIsoExtractionToTempAsync for {isoFileName}", ex);
            return false;
        }
        finally
        {
            // Clean up temp extraction directory
            if (Directory.Exists(tempExtractionDir))
            {
                var success = await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempExtractionDir, 5, 2000, _logger);
                if (!success)
                {
                    _logger.LogMessage($"    WARNING: Failed to clean up temp extraction directory: {Path.GetFileName(tempExtractionDir)}");
                }
            }

            await cancellationRegistration.DisposeAsync();
        }
    }
}