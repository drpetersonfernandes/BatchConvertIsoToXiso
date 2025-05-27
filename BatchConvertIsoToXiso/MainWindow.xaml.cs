using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace BatchConvertIsoToXiso;

public partial class MainWindow : IDisposable
{
    private CancellationTokenSource _cts;
    private readonly BugReportService _bugReportService;

    // Bug Report API configuration
    private const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string BugReportApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string ApplicationName = "BatchConvertIsoToXiso";

    private readonly List<string> _processOutputLines = new();

    private enum ConversionToolResultStatus
    {
        Success,
        Skipped,
        Failed
    }

    private enum FileProcessingStatus
    {
        Converted,
        Skipped,
        Failed
    }

    private sealed class ProcessableItem
    {
        public string FilePath { get; } // Path to the ISO file (original or extracted)
        public string? OriginalArchivePath { get; } // Path to the original archive, if this ISO was extracted
        public bool IsFromArchive => OriginalArchivePath != null;

        public ProcessableItem(string filePath, string? originalArchivePath = null)
        {
            FilePath = filePath;
            OriginalArchivePath = originalArchivePath;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        _cts = new CancellationTokenSource();
        _bugReportService = new BugReportService(BugReportApiUrl, BugReportApiKey, ApplicationName);

        LogMessage("Welcome to the Batch Convert ISO to XISO.");
        LogMessage("This program will convert ISO/XISO files (and ISOs within ZIP/7Z/RAR archives) to Xbox XISO format using extract-xiso.");
        LogMessage("Please follow these steps:");
        LogMessage("1. Select the input folder containing ISO files to convert");
        LogMessage("2. Select the output folder where converted XISO files will be saved");
        LogMessage("3. Choose whether to delete original files after conversion");
        LogMessage("4. Click 'Start Conversion' to begin the process");
        LogMessage("");

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;

        // Verify extract-xiso.exe
        var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");
        if (File.Exists(extractXisoPath))
        {
            LogMessage("extract-xiso.exe found in the application directory.");
        }
        else
        {
            LogMessage("WARNING: extract-xiso.exe not found. ISO conversion will fail.");
            Task.Run(async () => await ReportBugAsync("extract-xiso.exe not found."));
        }

        // Verify 7z.exe
        var sevenZipPath = Path.Combine(appDirectory, "7z.exe");
        if (File.Exists(sevenZipPath))
        {
            LogMessage("7z.exe found in the application directory. Archive extraction is available.");
        }
        else
        {
            LogMessage("WARNING: 7z.exe not found. Archive extraction (ZIP, 7Z, RAR) will not be available.");
            // Optionally report this, but it's a soft dependency for a feature.
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _cts.Cancel();
    }

    protected override void OnClosing(CancelEventArgs e) // More standard way to handle closing
    {
        _cts.Cancel();
        base.OnClosing(e);
    }

    private void LogMessage(string message)
    {
        var timestampedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogViewer.AppendText($"{timestampedMessage}{Environment.NewLine}");
            LogViewer.ScrollToEnd();
        });
    }

    private void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        var inputFolder = SelectFolder("Select the folder containing ISO or archive files");
        if (string.IsNullOrEmpty(inputFolder)) return;

        InputFolderTextBox.Text = inputFolder;
        LogMessage($"Input folder selected: {inputFolder}");
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var outputFolder = SelectFolder("Select the output folder for converted XISO files");
        if (string.IsNullOrEmpty(outputFolder)) return;

        OutputFolderTextBox.Text = outputFolder;
        LogMessage($"Output folder selected: {outputFolder}");
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");
            var sevenZipPath = Path.Combine(appDirectory, "7z.exe");

            if (!File.Exists(extractXisoPath))
            {
                LogMessage("Error: extract-xiso.exe not found.");
                ShowError("extract-xiso.exe is missing. Please ensure it's in the application folder.");
                _ = ReportBugAsync("extract-xiso.exe not found at start of conversion.", new FileNotFoundException("extract-xiso.exe missing", extractXisoPath));
                return;
            }

            var inputFolder = InputFolderTextBox.Text;
            var outputFolder = OutputFolderTextBox.Text;
            var deleteFiles = DeleteFilesCheckBox.IsChecked ?? false;

            if (string.IsNullOrEmpty(inputFolder) || string.IsNullOrEmpty(outputFolder))
            {
                ShowError("Please select both input and output folders.");
                return;
            }

            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }

            SetControlsState(false);
            LogMessage("Starting batch conversion process...");
            // ... (logging input parameters)

            try
            {
                await PerformBatchConversionAsync(extractXisoPath, sevenZipPath, inputFolder, outputFolder, deleteFiles);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Operation was canceled by user.");
            }
            catch (Exception ex)
            {
                LogMessage($"Critical Error: {ex.Message}");
                _ = ReportBugAsync("Critical error during batch conversion process", ex);
            }
            finally
            {
                SetControlsState(true);
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync($"Error during batch conversion process: {ex.Message}", ex);
            LogMessage($"Error during batch conversion process: {ex.Message}");
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        LogMessage("Cancellation requested. Finishing current file...");
    }

    private void SetControlsState(bool enabled)
    {
        InputFolderTextBox.IsEnabled = enabled;
        OutputFolderTextBox.IsEnabled = enabled;
        BrowseInputButton.IsEnabled = enabled;
        BrowseOutputButton.IsEnabled = enabled;
        DeleteFilesCheckBox.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
        ProgressBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (enabled)
        {
            ProgressBar.IsIndeterminate = false; // Reset indeterminate state
        }
    }

    private static string? SelectFolder(string description)
    {
        var dialog = new OpenFolderDialog { Title = description };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private async Task PerformBatchConversionAsync(string extractXisoPath, string sevenZipPath, string inputFolder, string outputFolder, bool deleteOriginals)
    {
        var processableIsoItems = new List<ProcessableItem>();
        var tempFoldersToClean = new List<string>();
        // Tracks results for ISOs from each archive, to decide if original archive can be deleted.
        // Key: Original Archive Path, Value: List of FileProcessingStatus for ISOs from that archive.
        var archiveFileResults = new Dictionary<string, List<FileProcessingStatus>>();
        var archivesFailedToExtract = 0;
        var sevenZipAvailable = File.Exists(sevenZipPath);

        if (!sevenZipAvailable)
        {
            LogMessage("WARNING: 7z.exe not found. Archive processing will be skipped.");
        }

        LogMessage("Scanning input folder and pre-processing archives...");
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = true);

        try
        {
            var initialFiles = Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext == ".iso" || (sevenZipAvailable && ext is ".zip" or ".7z" or ".rar");
                }).ToList();

            LogMessage($"Found {initialFiles.Count} potential ISO files or archives to process.");

            foreach (var filePath in initialFiles)
            {
                _cts.Token.ThrowIfCancellationRequested();

                var fileExtension = Path.GetExtension(filePath).ToLowerInvariant();

                if (fileExtension == ".iso")
                {
                    processableIsoItems.Add(new ProcessableItem(filePath));
                }
                else // Is an archive (.zip, .7z, .rar) and 7z.exe is available
                {
                    var archiveFileName = Path.GetFileName(filePath);
                    LogMessage($"Processing archive: {archiveFileName}");
                    archiveFileResults[filePath] = new List<FileProcessingStatus>(); // Initialize for this archive

                    var tempExtractionDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_Extract", Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempExtractionDir);
                    tempFoldersToClean.Add(tempExtractionDir);

                    var extractionSuccess = await ExtractArchiveAsync(sevenZipPath, filePath, tempExtractionDir);
                    if (extractionSuccess)
                    {
                        var extractedIsos = Directory.GetFiles(tempExtractionDir, "*.iso", SearchOption.AllDirectories);
                        if (extractedIsos.Length > 0)
                        {
                            LogMessage($"Found {extractedIsos.Length} ISO(s) in {archiveFileName}. Adding to queue.");
                            foreach (var extractedIsoPath in extractedIsos)
                            {
                                processableIsoItems.Add(new ProcessableItem(extractedIsoPath, filePath));
                            }
                        }
                        else
                        {
                            LogMessage($"No ISO files found in archive: {archiveFileName}.");
                            // Mark archive as "processed" (empty of ISOs) for deletion logic
                            archiveFileResults[filePath].Add(FileProcessingStatus.Skipped);
                        }
                    }
                    else
                    {
                        LogMessage($"Failed to extract archive: {archiveFileName}. It will be skipped.");
                        archivesFailedToExtract++;
                        archiveFileResults[filePath].Add(FileProcessingStatus.Failed); // Mark archive as failed extraction
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        } // Rethrow to be caught by StartButton_Click
        catch (Exception ex)
        {
            LogMessage($"Error during pre-scan/extraction phase: {ex.Message}");
            _ = ReportBugAsync("Error in pre-scan/extraction phase", ex);
        }
        finally
        {
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
        }

        if (_cts.Token.IsCancellationRequested)
        {
            CleanupTempFolders(tempFoldersToClean);
            throw new OperationCanceledException();
        }

        if (processableIsoItems.Count == 0)
        {
            LogMessage("No ISO files to convert (either standalone or found in archives).");
            if (deleteOriginals) HandleArchiveDeletion(archiveFileResults, processableIsoItems, deleteOriginals); // Delete empty successfully extracted archives
            CleanupTempFolders(tempFoldersToClean);
            ShowMessageBox("No ISO files found to process.", "Process Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LogMessage($"Total ISO files to process: {processableIsoItems.Count}");
        ProgressBar.Maximum = processableIsoItems.Count;
        ProgressBar.Value = 0;

        int successCount = 0, skippedCount = 0, failureCount = 0;

        for (var i = 0; i < processableIsoItems.Count; i++)
        {
            _cts.Token.ThrowIfCancellationRequested();

            var currentItem = processableIsoItems[i];
            var isoFileName = Path.GetFileName(currentItem.FilePath);
            var logPrefix = currentItem.IsFromArchive
                ? $"[{Path.GetFileName(currentItem.OriginalArchivePath!)} -> {isoFileName}]"
                : $"[{isoFileName}]";

            LogMessage($"[{i + 1}/{processableIsoItems.Count}] {logPrefix} Processing...");

            // If ISO is from archive, `deleteOriginals` for ConvertFileAsync should be false.
            // Original archive deletion is handled separately.
            var deleteThisIsoFile = deleteOriginals && !currentItem.IsFromArchive;
            var status = await ConvertFileAsync(extractXisoPath, currentItem.FilePath, outputFolder, deleteThisIsoFile);

            switch (status)
            {
                case FileProcessingStatus.Converted: successCount++; break;
                case FileProcessingStatus.Skipped: skippedCount++; break;
                case FileProcessingStatus.Failed: failureCount++; break;
            }

            if (currentItem.IsFromArchive)
            {
                archiveFileResults[currentItem.OriginalArchivePath!].Add(status);
            }

            ProgressBar.Value = i + 1;
        }

        LogMessage("\nBatch conversion summary:");
        LogMessage($"Successfully converted: {successCount} ISO files");
        LogMessage($"Skipped (already optimized): {skippedCount} ISO files");
        LogMessage($"Failed to process: {failureCount} ISO files");
        if (archivesFailedToExtract > 0) LogMessage($"Archives failed to extract: {archivesFailedToExtract}");

        ShowMessageBox($"Batch conversion completed.\n\n" +
                       $"Successfully converted: {successCount} ISO files\n" +
                       $"Skipped (already optimized): {skippedCount} ISO files\n" +
                       $"Failed to process: {failureCount} ISO files\n" +
                       (archivesFailedToExtract > 0 ? $"Archives failed to extract: {archivesFailedToExtract}\n" : ""),
            "Conversion Complete", MessageBoxButton.OK,
            (failureCount > 0 || archivesFailedToExtract > 0) ? MessageBoxImage.Warning : MessageBoxImage.Information);

        if (deleteOriginals)
        {
            HandleArchiveDeletion(archiveFileResults, processableIsoItems, deleteOriginals);
        }

        CleanupTempFolders(tempFoldersToClean);
    }

    private void HandleArchiveDeletion(Dictionary<string, List<FileProcessingStatus>> archiveFileResults, List<ProcessableItem> allProcessedIsos, bool deleteFlag)
    {
        if (!deleteFlag) return;

        LogMessage("Handling deletion of original archive files...");
        foreach (var archivePath in archiveFileResults.Keys)
        {
            var resultsForThisArchive = archiveFileResults[archivePath];

            // Condition for deleting an archive:
            // 1. It must have been attempted (i.e., has entries in resultsForThisArchive).
            // 2. All entries must be either Converted or Skipped (i.e., no Failed entries for its ISOs or extraction itself).
            var canDeleteArchive = resultsForThisArchive.Count != 0 &&
                                   resultsForThisArchive.All(static s => s is FileProcessingStatus.Converted or FileProcessingStatus.Skipped);

            if (canDeleteArchive)
            {
                LogMessage($"All contents of archive {Path.GetFileName(archivePath)} processed successfully. Deleting original archive.");
                TryDeleteFile(archivePath);
            }
            else
            {
                LogMessage($"Not deleting archive {Path.GetFileName(archivePath)} due to processing failures of its contents or extraction failure.");
            }
        }
    }

    private async Task<FileProcessingStatus> ConvertFileAsync(string extractXisoPath, string inputFile, string outputFolder, bool deleteOriginalIsoFile)
    {
        var fileName = Path.GetFileName(inputFile); // This is the ISO name
        var logPrefix = $"File '{fileName}':";
        try
        {
            var toolResult = await RunConversionToolAsync(extractXisoPath, inputFile);

            if (toolResult == ConversionToolResultStatus.Failed)
            {
                LogMessage($"{logPrefix} extract-xiso tool reported failure. This might be due to a corrupted ISO or an issue with the tool itself.");
                _ = ReportBugAsync($"extract-xiso tool reported failure for file: {fileName}. Check application log for tool output.");
                return FileProcessingStatus.Failed;
            }

            var destinationPath = Path.Combine(outputFolder, fileName); // Final destination for the ISO
            Directory.CreateDirectory(outputFolder); // Ensure output directory exists

            if (toolResult == ConversionToolResultStatus.Skipped)
            {
                LogMessage($"{logPrefix} Already optimized (skipped by extract-xiso). Copying to output.");
                await Task.Run(() => File.Copy(inputFile, destinationPath, true), _cts.Token);

                if (!deleteOriginalIsoFile) return FileProcessingStatus.Skipped; // This applies only to standalone ISOs

                LogMessage($"{logPrefix} Deleting original (skipped) file: {fileName}");
                await Task.Run(() => File.Delete(inputFile), _cts.Token);
                return FileProcessingStatus.Skipped;
            }

            // If ConversionToolResultStatus.Success (actual conversion took place by extract-xiso -r)
            // The original `inputFile` has been replaced by the converted version, and `inputFile.old` is the backup.
            var convertedFilePath = inputFile; // The new file is at the original inputFile path
            var originalBackupPath = inputFile + ".old";

            LogMessage($"{logPrefix} Moving converted file to output folder: {destinationPath}");
            await Task.Run(() => File.Move(convertedFilePath, destinationPath, true), _cts.Token);

            if (File.Exists(originalBackupPath))
            {
                if (deleteOriginalIsoFile) // This applies only to standalone ISOs
                {
                    LogMessage($"{logPrefix} Deleting original backup file: {Path.GetFileName(originalBackupPath)}");
                    await Task.Run(() => File.Delete(originalBackupPath), _cts.Token);
                }
                else
                {
                    LogMessage($"{logPrefix} Restoring original file from backup: {fileName}");
                    await Task.Run(() => File.Move(originalBackupPath, inputFile, true), _cts.Token);
                }
            }
            else
            {
                LogMessage($"{logPrefix} No .old backup file found after conversion. This is unexpected if extract-xiso -r ran successfully and modified the file.");
                // If deleteOriginalIsoFile was true, the original (which was converted in-place) is now moved.
                // If deleteOriginalIsoFile was false, the original (converted in-place) is moved, nothing to restore.
            }

            return FileProcessingStatus.Converted;
        }
        catch (OperationCanceledException)
        {
            LogMessage($"{logPrefix} Operation canceled during processing.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"{logPrefix} Error processing: {ex.Message}");
            _ = ReportBugAsync($"Error processing file: {fileName}", ex);
            return FileProcessingStatus.Failed;
        }
    }

    private async Task<ConversionToolResultStatus> RunConversionToolAsync(string extractXisoPath, string inputFile)
    {
        lock (_processOutputLines)
        {
            _processOutputLines.Clear();
        }

        var isoFileName = Path.GetFileName(inputFile);
        LogMessage($"Running extract-xiso on: {isoFileName}");

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = extractXisoPath,
                Arguments = $"-r \"{inputFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(inputFile) ?? AppDomain.CurrentDomain.BaseDirectory
            };

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null) return;

                LogMessage($"extract-xiso: {args.Data}");
                lock (_processOutputLines)
                {
                    _processOutputLines.Add(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data)) LogMessage($"extract-xiso error: {args.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var cancellationRegistration = _cts.Token.Register(() =>
            {
                if (process.HasExited) return;

                try
                {
                    process.Kill(true);
                }
                catch
                {
                    /* ignore */
                }
            });

            await process.WaitForExitAsync(_cts.Token);
            await cancellationRegistration.DisposeAsync();

            if (_cts.Token.IsCancellationRequested && process.ExitCode != 0 && process.ExitCode != 1) // 1 can be warning/skipped
            {
                LogMessage($"extract-xiso for {isoFileName} was canceled, exit code {process.ExitCode}.");
                throw new OperationCanceledException(_cts.Token);
            }

            if (process.ExitCode != 0 && process.ExitCode != 1) // extract-xiso might return 1 for skipped/already optimized
            {
                return ConversionToolResultStatus.Failed;
            }

            List<string> currentOutput;
            lock (_processOutputLines)
            {
                currentOutput = new List<string>(_processOutputLines);
            }

            // extract-xiso v2.7.1 output for skipped: "H:\path\to\file.iso is already optimized, skipping..."
            // extract-xiso might also just exit with 0 or 1 if it skips without explicit message depending on version/mode.
            // The -r command should ideally always try to rebuild. If it doesn't change the file, it's effectively "skipped" or "no change".
            // For simplicity, if exit code is 0 or 1, and output contains "skipping", it's Skipped. Otherwise Success.
            // If extract-xiso -r doesn't modify the file but exits 0, it's still a "success" in terms of tool run.
            // The crucial part is that the `inputFile` path now points to the (potentially) modified ISO.

            // Let's check for "is already optimized, skipping..." or similar.
            // If extract-xiso -r is used, it might not always say "skipping" but just rebuild it to the same state.
            // The old logic for checking "skipping" message is still good.
            if (currentOutput.Any(line => line.Contains("is already optimized, skipping...", StringComparison.OrdinalIgnoreCase) ||
                                          line.Contains("already an XISO image", StringComparison.OrdinalIgnoreCase))) // Add more skip phrases if known
            {
                return ConversionToolResultStatus.Skipped;
            }

            // If exit code is 0 or 1, and no explicit "skipping" message, assume it processed.
            // extract-xiso with -r might return 0 even if it did work.
            // If it returns 1 (warning) but didn't say skipping, it might still have worked.
            // This part is tricky without exact knowledge of all extract-xiso exit codes and outputs for -r.
            // Safest: if exit code 0 or 1, and no "skipping" message, assume it did its job (Success).
            // If it didn't create an .old file, our logic in ConvertFileAsync handles that.
            return ConversionToolResultStatus.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error running extract-xiso for {isoFileName}: {ex.Message}");
            _ = ReportBugAsync($"Exception during extract-xiso for {isoFileName}", ex);
            return ConversionToolResultStatus.Failed;
        }
    }

    private async Task<bool> ExtractArchiveAsync(string sevenZipPath, string archivePath, string extractionPath)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        LogMessage($"Extracting: {archiveFileName} using 7z.exe to {extractionPath}");

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments = $"x \"{archivePath}\" -o\"{extractionPath}\" -y -bsp0 -bso0", // x=extract with paths, -o=output, -y=yes, -bsp0/bso0=no progress
                RedirectStandardOutput = true, // Keep true to capture any messages
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };

            // Minimal logging for 7z unless errors occur to prevent log spam
            var errorMessages = new List<string>();
            process.OutputDataReceived += static (_, args) =>
            {
                if (args.Data != null)
                {
                    /* LogMessage($"7z: {args.Data}"); */
                }
            }; // Can be verbose
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrEmpty(args.Data)) return;

                errorMessages.Add(args.Data);
                LogMessage($"7z error: {args.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var cancellationRegistration = _cts.Token.Register(() =>
            {
                if (process.HasExited) return;

                try
                {
                    process.Kill(true);
                }
                catch
                {
                    /* ignore */
                }
            });

            await process.WaitForExitAsync(_cts.Token);
            await cancellationRegistration.DisposeAsync();

            // 7-Zip Exit Codes: 0 = No error, 1 = Warning, 2 = Fatal error
            // 7 = Command line error, 8 = Not enough memory, 255 = User stopped process
            if (_cts.Token.IsCancellationRequested && process.ExitCode != 0)
            {
                LogMessage($"Extraction of {archiveFileName} canceled, 7z exit code: {process.ExitCode}.");
                throw new OperationCanceledException(_cts.Token);
            }

            var success = process.ExitCode is 0 or 1; // Treat warnings as non-fatal for extraction completion
            if (success)
            {
                LogMessage($"Successfully extracted: {archiveFileName}");
                return true;
            }
            else
            {
                LogMessage($"7z.exe failed to extract {archiveFileName}. Exit Code: {process.ExitCode}. Errors: {string.Join("; ", errorMessages)}");
                _ = ReportBugAsync($"7z.exe failed for {archiveFileName}. Exit: {process.ExitCode}", new Exception($"7z Errors: {string.Join("\n", errorMessages)}"));
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Exception during extraction of {archiveFileName}: {ex.Message}");
            _ = ReportBugAsync($"Exception extracting {archiveFileName}", ex);
            return false;
        }
    }

    private void CleanupTempFolders(List<string> tempFolders)
    {
        LogMessage("Cleaning up temporary extraction folders...");
        foreach (var folder in tempFolders)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                    // LogMessage($"Cleaned up: {folder}"); // Can be verbose
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error cleaning temp folder {folder}: {ex.Message}");
            }
        }

        LogMessage("Temporary folder cleanup complete.");
    }

    private void TryDeleteFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            File.Delete(filePath);
            LogMessage($"Deleted: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            LogMessage($"Error deleting file {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    private void ShowMessageBox(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        Dispatcher.Invoke(() => MessageBox.Show(this, message, title, buttons, icon));
    }

    private void ShowError(string message)
    {
        ShowMessageBox(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async Task ReportBugAsync(string message, Exception? exception = null)
    {
        try
        {
            var fullReport = new StringBuilder();
            fullReport.AppendLine("=== Bug Report ===");
            fullReport.AppendLine($"Application: {ApplicationName}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $"Version: {GetType().Assembly.GetName().Version}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $"OS: {Environment.OSVersion}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $".NET Version: {Environment.Version}");
            fullReport.AppendLine(CultureInfo.InvariantCulture, $"Date/Time: {DateTime.Now}");
            fullReport.AppendLine();
            fullReport.AppendLine("=== Error Message ===");
            fullReport.AppendLine(message);
            fullReport.AppendLine();
            if (exception != null)
            {
                fullReport.AppendLine("=== Exception Details ===");
                fullReport.AppendLine(CultureInfo.InvariantCulture, $"Type: {exception.GetType().FullName}");
                fullReport.AppendLine(CultureInfo.InvariantCulture, $"Message: {exception.Message}");
                fullReport.AppendLine(CultureInfo.InvariantCulture, $"Source: {exception.Source}");
                fullReport.AppendLine("Stack Trace:");
                fullReport.AppendLine(exception.StackTrace);
                if (exception.InnerException != null)
                {
                    fullReport.AppendLine("Inner Exception:");
                    fullReport.AppendLine(CultureInfo.InvariantCulture, $"Type: {exception.InnerException.GetType().FullName}");
                    fullReport.AppendLine(CultureInfo.InvariantCulture, $"Message: {exception.InnerException.Message}");
                    fullReport.AppendLine("Stack Trace:");
                    fullReport.AppendLine(exception.InnerException.StackTrace);
                }
            }

            if (LogViewer != null)
            {
                var logContent = string.Empty;
                await Dispatcher.InvokeAsync(() => { logContent = LogViewer.Text; });
                if (!string.IsNullOrEmpty(logContent))
                {
                    fullReport.AppendLine();
                    fullReport.AppendLine("=== Application Log ===");
                    fullReport.Append(logContent);
                }
            }

            await _bugReportService.SendBugReportAsync(fullReport.ToString());
        }
        catch
        {
            /* Silently fail bug reporting */
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _bugReportService?.Dispose();
        GC.SuppressFinalize(this);
    }
}