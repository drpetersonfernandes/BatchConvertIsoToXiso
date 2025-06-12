using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Threading; 

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

    // Summary Stats
    private DateTime _conversionStartTime;
    private readonly DispatcherTimer _processingTimer;
    private int _uiTotalFiles; // Total ISOs expected to be processed (dynamically updated)
    private int _uiSuccessCount; // ISOs successfully converted or skipped
    private int _uiFailedCount; // ISOs that failed conversion

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

    public MainWindow()
    {
        InitializeComponent();
        _cts = new CancellationTokenSource();
        _bugReportService = new BugReportService(BugReportApiUrl, BugReportApiKey, ApplicationName);

        _processingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _processingTimer.Tick += ProcessingTimer_Tick;
        ResetSummaryStats();

        LogMessage("Welcome to the Batch Convert ISO to XISO.");
        LogMessage("This program will convert ISO/XISO files (and ISOs within ZIP/7Z/RAR archives) to Xbox XISO format using extract-xiso.");
        LogMessage("Please follow these steps:");
        LogMessage("1. Select the input folder containing ISO files to convert");
        LogMessage("2. Select the output folder where converted XISO files will be saved");
        LogMessage("3. Choose whether to delete original files after conversion");
        LogMessage("4. Click 'Start Conversion' to begin the process");
        LogMessage("");

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;

        var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");
        if (File.Exists(extractXisoPath))
        {
            LogMessage("extract-xiso.exe found in the application directory.");
        }
        else
        {
            LogMessage("WARNING: extract-xiso.exe not found. ISO conversion will fail.");
            Task.Run(() => Task.FromResult(_ = ReportBugAsync("extract-xiso.exe not found.")));
        }

        var sevenZipPath = Path.Combine(appDirectory, "7z.exe");
        if (File.Exists(sevenZipPath))
        {
            LogMessage("7z.exe found in the application directory. Archive extraction is available.");
        }
        else
        {
            LogMessage("WARNING: 7z.exe not found. Archive extraction (ZIP, 7Z, RAR) will not be available.");
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _cts.Cancel();
    }

    protected override void OnClosing(CancelEventArgs e)
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

            ResetSummaryStats();
            _conversionStartTime = DateTime.Now;
            _processingTimer.Start();

            SetControlsState(false);
            LogMessage("Starting batch conversion process...");

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
                _processingTimer.Stop();
                var finalElapsedTime = DateTime.Now - _conversionStartTime;
                ProcessingTimeValue.Text = finalElapsedTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
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
        LogMessage("Cancellation requested. Finishing current file/archive...");
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
            ProgressBar.IsIndeterminate = false;
        }
    }

    private static string? SelectFolder(string description)
    {
        var dialog = new OpenFolderDialog { Title = description };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void ResetSummaryStats()
    {
        _uiTotalFiles = 0;
        _uiSuccessCount = 0;
        _uiFailedCount = 0;
        UpdateSummaryStatsUi(); // Initial update
        Application.Current.Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = 0;
            ProgressBar.Maximum = 1; // Default to 1 to avoid division by zero or invisible bar
        });
        ProcessingTimeValue.Text = "00:00:00";
    }

    private void UpdateSummaryStatsUi(int? newTotalFiles = null, int? newProgressMax = null)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (newTotalFiles.HasValue)
            {
                _uiTotalFiles = newTotalFiles.Value;
            }

            TotalFilesValue.Text = _uiTotalFiles.ToString(CultureInfo.InvariantCulture);
            SuccessValue.Text = _uiSuccessCount.ToString(CultureInfo.InvariantCulture);
            FailedValue.Text = _uiFailedCount.ToString(CultureInfo.InvariantCulture);
            if (newProgressMax.HasValue)
            {
                ProgressBar.Maximum = newProgressMax.Value > 0 ? newProgressMax.Value : 1;
            }
        });
    }


    private void ProcessingTimer_Tick(object? sender, EventArgs e)
    {
        var elapsedTime = DateTime.Now - _conversionStartTime;
        ProcessingTimeValue.Text = elapsedTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private async Task PerformBatchConversionAsync(string extractXisoPath, string sevenZipPath, string inputFolder, string outputFolder, bool deleteOriginals)
    {
        var tempFoldersToCleanUpAtEnd = new List<string>();
        var archivesFailedToExtractOrProcess = 0;
        var sevenZipAvailable = File.Exists(sevenZipPath);

        var overallIsosSuccessfullyConverted = 0;
        var overallIsosSkipped = 0;
        var overallIsosFailed = 0;
        var actualIsosProcessedForProgress = 0;

        // Reset UI stats (already done in StartButton_Click, but good to ensure)
        _uiSuccessCount = 0;
        _uiFailedCount = 0;
        ProgressBar.Value = 0;

        if (!sevenZipAvailable)
        {
            LogMessage("WARNING: 7z.exe not found. Archive processing will be skipped.");
        }

        LogMessage("Scanning input folder for items to process...");
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = true);

        List<string> initialEntriesToProcess;
        try
        {
            initialEntriesToProcess = Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext == ".iso" || (sevenZipAvailable && ext is ".zip" or ".7z" or ".rar");
                }).ToList();
        }
        catch (Exception ex)
        {
            LogMessage($"Error scanning input folder: {ex.Message}");
            _ = ReportBugAsync("Error scanning input folder", ex);
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
            return;
        }

        if (initialEntriesToProcess.Count == 0)
        {
            LogMessage("No ISO files or supported archives found in the input folder.");
            ShowMessageBox("No ISO files or supported archives found.", "Process Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
            return;
        }

        LogMessage($"Found {initialEntriesToProcess.Count} top-level items (ISOs or archives).");
        var currentExpectedTotalIsos = initialEntriesToProcess.Count; // Initial estimate
        UpdateSummaryStatsUi(currentExpectedTotalIsos, currentExpectedTotalIsos);
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
        LogMessage($"Starting conversion... Total items to process initially: {currentExpectedTotalIsos}. This may increase if archives contain multiple ISOs.");


        // --- Main Processing Loop ---
        foreach (var currentEntryPath in initialEntriesToProcess)
        {
            _cts.Token.ThrowIfCancellationRequested();

            var entryFileName = Path.GetFileName(currentEntryPath);
            var entryExtension = Path.GetExtension(currentEntryPath).ToLowerInvariant();

            if (entryExtension == ".iso")
            {
                LogMessage($"Processing standalone ISO: {entryFileName}...");

                var status = await ConvertFileAsync(extractXisoPath, currentEntryPath, outputFolder, deleteOriginals);
                actualIsosProcessedForProgress++;
                switch (status)
                {
                    case FileProcessingStatus.Converted:
                        overallIsosSuccessfullyConverted++;
                        _uiSuccessCount++;
                        break;
                    case FileProcessingStatus.Skipped:
                        overallIsosSkipped++;
                        _uiSuccessCount++;
                        break;
                    case FileProcessingStatus.Failed:
                        overallIsosFailed++;
                        _uiFailedCount++;
                        break;
                }

                UpdateSummaryStatsUi(); // Only update counts, not total/max here
                ProgressBar.Value = actualIsosProcessedForProgress;
            }
            else if (sevenZipAvailable && entryExtension is ".zip" or ".7z" or ".rar")
            {
                LogMessage($"Processing archive: {entryFileName}...");
                var statusesOfIsosInThisArchive = new List<FileProcessingStatus>();
                string? currentArchiveTempExtractionDir = null;
                var archiveExtractedSuccessfully = false;

                try
                {
                    currentArchiveTempExtractionDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_Extract", Guid.NewGuid().ToString());
                    Directory.CreateDirectory(currentArchiveTempExtractionDir);
                    tempFoldersToCleanUpAtEnd.Add(currentArchiveTempExtractionDir);

                    archiveExtractedSuccessfully = await ExtractArchiveAsync(sevenZipPath, currentEntryPath, currentArchiveTempExtractionDir);
                    if (archiveExtractedSuccessfully)
                    {
                        var extractedIsoFiles = Directory.GetFiles(currentArchiveTempExtractionDir, "*.iso", SearchOption.AllDirectories);

                        // Dynamically update total expected ISOs and progress bar maximum
                        if (extractedIsoFiles.Length > 0)
                        {
                            // The archive itself (1 item) is being replaced by N ISOs.
                            // So, the net change to total items is (N - 1).
                            var newIsosFound = extractedIsoFiles.Length;
                            currentExpectedTotalIsos += (newIsosFound - 1); // Adjust total
                            UpdateSummaryStatsUi(currentExpectedTotalIsos, currentExpectedTotalIsos);
                            LogMessage($"Found {newIsosFound} ISO(s) in {entryFileName}. Total expected ISOs now: {currentExpectedTotalIsos}. Processing them now...");
                        }
                        else if (extractedIsoFiles.Length == 0) // No ISOs in archive
                        {
                             // The archive was counted as 1 item, and it yields 0 ISOs.
                             // So, effectively, 1 item is "processed" without yielding ISOs.
                             // We should decrement the expected total if it was counted as a potential ISO source.
                             // And increment actualIsosProcessedForProgress as the archive itself is "done".
                             LogMessage($"No ISO files found in archive: {entryFileName}.");
                             actualIsosProcessedForProgress++; // Count the archive itself as processed
                             ProgressBar.Value = actualIsosProcessedForProgress;
                             // No change to _uiSuccessCount or _uiFailedCount for an empty archive
                        }


                        foreach (var extractedIsoPath in extractedIsoFiles)
                        {
                            _cts.Token.ThrowIfCancellationRequested();
                            var extractedIsoName = Path.GetFileName(extractedIsoPath);
                            LogMessage($"  Converting ISO from archive: {extractedIsoName}...");

                            var status = await ConvertFileAsync(extractXisoPath, extractedIsoPath, outputFolder, false);
                            statusesOfIsosInThisArchive.Add(status);
                            actualIsosProcessedForProgress++;

                            switch (status)
                            {
                                case FileProcessingStatus.Converted:
                                    overallIsosSuccessfullyConverted++;
                                    _uiSuccessCount++;
                                    break;
                                case FileProcessingStatus.Skipped:
                                    overallIsosSkipped++;
                                    _uiSuccessCount++;
                                    break;
                                case FileProcessingStatus.Failed:
                                    overallIsosFailed++;
                                    _uiFailedCount++;
                                    break;
                            }

                            UpdateSummaryStatsUi(); // Update counts
                            ProgressBar.Value = actualIsosProcessedForProgress;
                        }

                        if (extractedIsoFiles.Length == 0) // If archive was empty, mark it as skipped for deletion logic
                        {
                            statusesOfIsosInThisArchive.Add(FileProcessingStatus.Skipped);
                        }
                    }
                    else // Archive extraction failed
                    {
                        LogMessage($"Failed to extract archive: {entryFileName}. It will be skipped.");
                        archivesFailedToExtractOrProcess++;
                        statusesOfIsosInThisArchive.Add(FileProcessingStatus.Failed);
                        actualIsosProcessedForProgress++; // Count the archive itself as "processed" (failed)
                        _uiFailedCount++; // Count the archive itself as a failed item in UI
                        UpdateSummaryStatsUi();
                        ProgressBar.Value = actualIsosProcessedForProgress;
                    }

                    if (deleteOriginals && archiveExtractedSuccessfully)
                    {
                        var allIsosFromArchiveOk = statusesOfIsosInThisArchive.Count > 0 &&
                                                   statusesOfIsosInThisArchive.All(s => s is FileProcessingStatus.Converted or FileProcessingStatus.Skipped);
                        if (allIsosFromArchiveOk)
                        {
                            LogMessage($"All contents of archive {entryFileName} processed successfully. Deleting original archive.");
                            TryDeleteFile(currentEntryPath);
                        }
                        else if (statusesOfIsosInThisArchive.Count > 0) // Some ISOs processed, but not all successfully
                        {
                            LogMessage($"Not deleting archive {entryFileName} due to processing issues with its contents.");
                        }
                        // If statusesOfIsosInThisArchive is empty (e.g. extraction failed before finding ISOs), don't delete.
                    }
                    else if (deleteOriginals && !archiveExtractedSuccessfully) // Extraction failed
                    {
                        LogMessage($"Not deleting archive {entryFileName} due to extraction failure.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogMessage($"Error processing archive {entryFileName}: {ex.Message}");
                    _ = ReportBugAsync($"Error during processing of archive {entryFileName}", ex);
                    archivesFailedToExtractOrProcess++;
                    // If an error occurs mid-archive processing, ensure progress reflects the attempt
                    if (!archiveExtractedSuccessfully)
                    {
                        actualIsosProcessedForProgress++; // Count the archive itself
                        _uiFailedCount++;
                        UpdateSummaryStatsUi();
                        ProgressBar.Value = actualIsosProcessedForProgress;
                    }
                }
                finally
                {
                    if (currentArchiveTempExtractionDir != null && Directory.Exists(currentArchiveTempExtractionDir))
                    {
                        try
                        {
                            Directory.Delete(currentArchiveTempExtractionDir, true);
                            tempFoldersToCleanUpAtEnd.Remove(currentArchiveTempExtractionDir);
                            LogMessage($"Cleaned up temporary folder for {entryFileName}.");
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"Error cleaning temp folder {currentArchiveTempExtractionDir} for {entryFileName}: {ex.Message}. Will retry at end.");
                        }
                    }
                }
            }
        }

        // Ensure progress bar reaches maximum if all items were processed, even if total was adjusted.
        if (!_cts.Token.IsCancellationRequested && actualIsosProcessedForProgress >= ProgressBar.Maximum)
        {
            ProgressBar.Value = ProgressBar.Maximum;
        }


        LogMessage("\nBatch conversion summary:");
        LogMessage($"Successfully converted: {overallIsosSuccessfullyConverted} ISO files");
        LogMessage($"Skipped (already optimized): {overallIsosSkipped} ISO files");
        LogMessage($"Failed to process: {overallIsosFailed} ISO files");
        if (archivesFailedToExtractOrProcess > 0) LogMessage($"Archives failed to extract or had processing errors: {archivesFailedToExtractOrProcess}");

        ShowMessageBox($"Batch conversion completed.\n\n" +
                       $"Successfully converted: {overallIsosSuccessfullyConverted} ISO files\n" +
                       $"Skipped (already optimized): {overallIsosSkipped} ISO files\n" +
                       $"Failed to process: {overallIsosFailed} ISO files\n" +
                       (archivesFailedToExtractOrProcess > 0 ? $"Archives failed to extract/process: {archivesFailedToExtractOrProcess}\n" : ""),
            "Conversion Complete", MessageBoxButton.OK,
            (overallIsosFailed > 0 || archivesFailedToExtractOrProcess > 0) ? MessageBoxImage.Warning : MessageBoxImage.Information);

        CleanupTempFolders(tempFoldersToCleanUpAtEnd);
    }

    private async Task<FileProcessingStatus> ConvertFileAsync(string extractXisoPath, string inputFile, string outputFolder, bool deleteOriginalIsoFile)
    {
        var fileName = Path.GetFileName(inputFile);
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

            var destinationPath = Path.Combine(outputFolder, fileName);
            Directory.CreateDirectory(outputFolder);

            if (toolResult == ConversionToolResultStatus.Skipped)
            {
                LogMessage($"{logPrefix} Already optimized (skipped by extract-xiso). Copying to output.");
                await Task.Run(() => File.Copy(inputFile, destinationPath, true), _cts.Token);

                if (!deleteOriginalIsoFile) return FileProcessingStatus.Skipped;

                LogMessage($"{logPrefix} Deleting original (skipped) file: {fileName}");
                await Task.Run(() => File.Delete(inputFile), _cts.Token);
                return FileProcessingStatus.Skipped;
            }

            var convertedFilePath = inputFile;
            var originalBackupPath = inputFile + ".old";

            LogMessage($"{logPrefix} Moving converted file to output folder: {destinationPath}");
            await Task.Run(() => File.Move(convertedFilePath, destinationPath, true), _cts.Token);

            if (File.Exists(originalBackupPath))
            {
                if (deleteOriginalIsoFile)
                {
                    LogMessage($"{logPrefix} Deleting original backup file: {Path.GetFileName(originalBackupPath)}");
                    await Task.Run(() => File.Delete(originalBackupPath), _cts.Token);
                }
                else
                {
                    LogMessage($"{logPrefix} Restoring original file from backup: {fileName} (as original was not to be deleted or was temporary)");
                    await Task.Run(() => File.Move(originalBackupPath, inputFile, true), _cts.Token);
                }
            }
            else if (deleteOriginalIsoFile)
            {
                LogMessage($"{logPrefix} Converted file moved. Original (in-place converted) is now at destination. No separate .old file to delete.");
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

        Process? processRef;

        try
        {
            using var process = new Process();
            processRef = process;
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
                if (processRef?.HasExited != false) return;

                try
                {
                    processRef.Kill(true);
                }
                catch
                {
                    /* ignore */
                }
            });

            await process.WaitForExitAsync(_cts.Token);
            await cancellationRegistration.DisposeAsync();

            if (_cts.Token.IsCancellationRequested && process.ExitCode != 0 && process.ExitCode != 1)
            {
                LogMessage($"extract-xiso for {isoFileName} was canceled, exit code {process.ExitCode}.");
                throw new OperationCanceledException(_cts.Token);
            }

            if (process.ExitCode != 0 && process.ExitCode != 1)
            {
                return ConversionToolResultStatus.Failed;
            }

            List<string> currentOutput;
            lock (_processOutputLines)
            {
                currentOutput = new List<string>(_processOutputLines);
            }

            if (currentOutput.Any(line => line.Contains("is already optimized, skipping...", StringComparison.OrdinalIgnoreCase) ||
                                          line.Contains("already an XISO image", StringComparison.OrdinalIgnoreCase)))
            {
                return ConversionToolResultStatus.Skipped;
            }

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

    private async Task<bool> ExtractArchiveAsync(string sevenZipPath, string archivePath, string extractionPath) // Removed silent param
    {
        var archiveFileName = Path.GetFileName(archivePath);
        LogMessage($"Extracting: {archiveFileName} using 7z.exe to {extractionPath}");

        Process? processRef;

        try
        {
            using var process = new Process();
            processRef = process;
            process.StartInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments = $"x \"{archivePath}\" -o\"{extractionPath}\" -y -bsp0 -bso0",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };

            var errorMessages = new List<string>();
            process.OutputDataReceived += (_, args) =>
            {
                // if (args.Data != null) { /* LogMessage($"7z: {args.Data}"); */ } // Can be very verbose
            };
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
                if (processRef?.HasExited != false) return;

                try
                {
                    processRef.Kill(true);
                }
                catch
                {
                    /* ignore */
                }
            });

            await process.WaitForExitAsync(_cts.Token);
            await cancellationRegistration.DisposeAsync();

            if (_cts.Token.IsCancellationRequested && process.ExitCode != 0)
            {
                LogMessage($"Extraction of {archiveFileName} canceled, 7z exit code: {process.ExitCode}.");
                throw new OperationCanceledException(_cts.Token);
            }

            var success = process.ExitCode is 0 or 1;
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
        if (tempFolders.Count == 0) return;

        LogMessage("Cleaning up remaining temporary extraction folders...");
        foreach (var folder in tempFolders)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error cleaning temp folder {folder}: {ex.Message}");
            }
        }

        tempFolders.Clear();
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
        _processingTimer.Tick -= ProcessingTimer_Tick;
        _processingTimer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _bugReportService?.Dispose();
        GC.SuppressFinalize(this);
    }
}
