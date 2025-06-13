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

    // Summary Stats
    private DateTime _conversionStartTime;
    private readonly DispatcherTimer _processingTimer;
    private int _uiTotalFiles;
    private int _uiSuccessCount;
    private int _uiFailedCount;

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

    private enum IsoTestResultStatus
    {
        Passed,
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
        LogMessage("It can also test the integrity of ISO files by attempting a full extraction to a temporary location.");
        LogMessage("Please follow these steps:");
        LogMessage("1. Select the input folder containing ISO files");
        LogMessage("2. For conversion, select the output folder where converted XISO files will be saved");
        LogMessage("3. For conversion, choose whether to delete original files after conversion");
        LogMessage("4. Click 'Start Conversion' or 'Test ISOs'");
        LogMessage("");

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;

        var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");
        if (File.Exists(extractXisoPath))
        {
            LogMessage("extract-xiso.exe found in the application directory.");
            _ = DiagnoseExtractXisoAsync(extractXisoPath);
        }
        else
        {
            LogMessage("WARNING: extract-xiso.exe not found. ISO conversion and testing will fail.");
            Task.Run(() => Task.FromResult(_ = ReportBugAsync("extract-xiso.exe not found.")));
        }

        var sevenZipPath = Path.Combine(appDirectory, "7z.exe");
        if (File.Exists(sevenZipPath))
        {
            LogMessage("7z.exe found in the application directory. Archive extraction is available for CONVERSION.");
        }
        else
        {
            LogMessage("WARNING: 7z.exe not found. Archive extraction (ZIP, 7Z, RAR) will not be available for CONVERSION.");
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
            Application.Current.Dispatcher.Invoke(() => LogViewer.Clear());

            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");
            var sevenZipPath = Path.Combine(appDirectory, "7z.exe");

            if (!await Task.Run(() => File.Exists(extractXisoPath), _cts.Token))
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

    private async void TestIsosButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() => LogViewer.Clear());

            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");

            if (!await Task.Run(() => File.Exists(extractXisoPath), _cts.Token))
            {
                LogMessage("Error: extract-xiso.exe not found.");
                ShowError("extract-xiso.exe is missing. Please ensure it's in the application folder.");
                _ = ReportBugAsync("extract-xiso.exe not found at start of ISO test.", new FileNotFoundException("extract-xiso.exe missing", extractXisoPath));
                return;
            }

            var inputFolder = InputFolderTextBox.Text;

            if (string.IsNullOrEmpty(inputFolder))
            {
                ShowError("Please select the input folder.");
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
            LogMessage("Starting batch ISO test process (only direct .iso files)...");

            try
            {
                await PerformBatchIsoTestAsync(extractXisoPath, inputFolder);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Operation was canceled by user.");
            }
            catch (Exception ex)
            {
                LogMessage($"Critical Error during ISO test: {ex.Message}");
                _ = ReportBugAsync("Critical error during batch ISO test process", ex);
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
            _ = ReportBugAsync($"Error during batch ISO test process: {ex.Message}", ex);
            LogMessage($"Error during batch ISO test process: {ex.Message}");
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
        TestIsosButton.IsEnabled = enabled;
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
        UpdateSummaryStatsUi();
        Application.Current.Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = 0;
            ProgressBar.Maximum = 1;
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
        var sevenZipAvailable = await Task.Run(() => File.Exists(sevenZipPath), _cts.Token);

        var overallIsosSuccessfullyConverted = 0;
        var overallIsosSkipped = 0;
        var overallIsosFailed = 0;
        var actualIsosProcessedForProgress = 0;
        var failedConversionFilePaths = new List<string>();

        _uiSuccessCount = 0;
        _uiFailedCount = 0;
        ProgressBar.Value = 0;

        if (!sevenZipAvailable)
        {
            LogMessage("WARNING: 7z.exe not found. Archive processing will be skipped for conversion.");
        }

        LogMessage("Scanning input folder for items to convert...");
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = true);

        List<string> initialEntriesToProcess;
        try
        {
            initialEntriesToProcess = await Task.Run(() =>
                    Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f =>
                        {
                            var ext = Path.GetExtension(f).ToLowerInvariant();
                            return ext == ".iso" || (sevenZipAvailable && ext is ".zip" or ".7z" or ".rar");
                        }).ToList(),
                _cts.Token);
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
            LogMessage("No ISO files or supported archives found in the input folder for conversion.");
            ShowMessageBox("No ISO files or supported archives found for conversion.", "Process Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
            return;
        }

        LogMessage($"Found {initialEntriesToProcess.Count} top-level items (ISOs or archives) for conversion.");
        var currentExpectedTotalIsos = initialEntriesToProcess.Count;
        UpdateSummaryStatsUi(currentExpectedTotalIsos, currentExpectedTotalIsos);
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
        LogMessage($"Starting conversion... Total items to process initially: {currentExpectedTotalIsos}. This may increase if archives contain multiple ISOs.");


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
                        failedConversionFilePaths.Add(currentEntryPath);
                        break;
                }

                UpdateSummaryStatsUi();
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
                    await Task.Run(() => Directory.CreateDirectory(currentArchiveTempExtractionDir), _cts.Token);
                    tempFoldersToCleanUpAtEnd.Add(currentArchiveTempExtractionDir);

                    archiveExtractedSuccessfully = await ExtractArchiveAsync(sevenZipPath, currentEntryPath, currentArchiveTempExtractionDir);
                    if (archiveExtractedSuccessfully)
                    {
                        var extractedIsoFiles = await Task.Run(() => Directory.GetFiles(currentArchiveTempExtractionDir, "*.iso", SearchOption.AllDirectories), _cts.Token);

                        if (extractedIsoFiles.Length > 0)
                        {
                            var newIsosFound = extractedIsoFiles.Length;
                            currentExpectedTotalIsos += (newIsosFound - 1);
                            UpdateSummaryStatsUi(currentExpectedTotalIsos, currentExpectedTotalIsos);
                            LogMessage($"Found {newIsosFound} ISO(s) in {entryFileName}. Total expected ISOs now: {currentExpectedTotalIsos}. Processing them now...");
                        }
                        else if (extractedIsoFiles.Length == 0)
                        {
                            LogMessage($"No ISO files found in archive: {entryFileName}.");
                            actualIsosProcessedForProgress++;
                            ProgressBar.Value = actualIsosProcessedForProgress;
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
                                    failedConversionFilePaths.Add(extractedIsoPath);
                                    break;
                            }

                            UpdateSummaryStatsUi();
                            ProgressBar.Value = actualIsosProcessedForProgress;
                        }

                        if (extractedIsoFiles.Length == 0)
                        {
                            statusesOfIsosInThisArchive.Add(FileProcessingStatus.Skipped);
                        }
                    }
                    else
                    {
                        LogMessage($"Failed to extract archive: {entryFileName}. It will be skipped.");
                        archivesFailedToExtractOrProcess++;
                        statusesOfIsosInThisArchive.Add(FileProcessingStatus.Failed);
                        failedConversionFilePaths.Add(currentEntryPath);
                        actualIsosProcessedForProgress++;
                        _uiFailedCount++;
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
                            await TryDeleteFileAsync(currentEntryPath);
                        }
                        else if (statusesOfIsosInThisArchive.Count > 0)
                        {
                            LogMessage($"Not deleting archive {entryFileName} due to processing issues with its contents.");
                        }
                    }
                    else if (deleteOriginals && !archiveExtractedSuccessfully)
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
                    failedConversionFilePaths.Add(currentEntryPath);
                    if (!archiveExtractedSuccessfully)
                    {
                        actualIsosProcessedForProgress++;
                        _uiFailedCount++;
                        UpdateSummaryStatsUi();
                        ProgressBar.Value = actualIsosProcessedForProgress;
                    }
                }
                finally
                {
                    if (currentArchiveTempExtractionDir != null && await Task.Run(() => Directory.Exists(currentArchiveTempExtractionDir), _cts.Token))
                    {
                        try
                        {
                            await Task.Run(() => Directory.Delete(currentArchiveTempExtractionDir, true), _cts.Token);
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

        if (!_cts.Token.IsCancellationRequested && actualIsosProcessedForProgress >= ProgressBar.Maximum)
        {
            ProgressBar.Value = ProgressBar.Maximum;
        }


        LogMessage("\nBatch conversion summary:");
        LogMessage($"Successfully converted: {overallIsosSuccessfullyConverted} ISO files");
        LogMessage($"Skipped (already optimized): {overallIsosSkipped} ISO files");
        LogMessage($"Failed to process: {overallIsosFailed} ISO files");
        if (archivesFailedToExtractOrProcess > 0) LogMessage($"Archives failed to extract or had processing errors: {archivesFailedToExtractOrProcess}");

        if (failedConversionFilePaths.Count > 0)
        {
            LogMessage("\nList of items that failed conversion or archive extraction:");
            foreach (var failedPath in failedConversionFilePaths)
            {
                LogMessage($"- {Path.GetFileName(failedPath)} (Full path: {failedPath})");
            }
        }

        ShowMessageBox($"Batch conversion completed.\n\n" +
                       $"Successfully converted: {overallIsosSuccessfullyConverted} ISO files\n" +
                       $"Skipped (already optimized): {overallIsosSkipped} ISO files\n" +
                       $"Failed to process: {overallIsosFailed} ISO files\n" +
                       (archivesFailedToExtractOrProcess > 0 ? $"Archives failed to extract/process: {archivesFailedToExtractOrProcess}\n" : ""),
            "Conversion Complete", MessageBoxButton.OK,
            (overallIsosFailed > 0 || archivesFailedToExtractOrProcess > 0) ? MessageBoxImage.Warning : MessageBoxImage.Information);

        await CleanupTempFoldersAsync(tempFoldersToCleanUpAtEnd);
    }

    private async Task PerformBatchIsoTestAsync(string extractXisoPath, string inputFolder)
    {
        var overallIsosTestPassed = 0;
        var overallIsosTestFailed = 0;
        var actualIsosProcessedForProgress = 0;
        var failedIsoOriginalPaths = new List<string>();

        _uiSuccessCount = 0;
        _uiFailedCount = 0;
        ProgressBar.Value = 0;

        LogMessage("Scanning input folder for .iso files to test...");
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = true);

        List<string> isoFilesToTest;
        try
        {
            isoFilesToTest = await Task.Run(() => Directory.GetFiles(inputFolder, "*.iso", SearchOption.TopDirectoryOnly).ToList(), _cts.Token);
        }
        catch (Exception ex)
        {
            LogMessage($"Error scanning input folder for .iso files: {ex.Message}");
            _ = ReportBugAsync("Error scanning input folder for .iso files for testing", ex);
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
            return;
        }

        if (isoFilesToTest.Count == 0)
        {
            LogMessage("No .iso files found in the input folder for testing.");
            ShowMessageBox("No .iso files found for testing.", "Test Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
            return;
        }

        LogMessage($"Found {isoFilesToTest.Count} .iso files for testing.");
        UpdateSummaryStatsUi(isoFilesToTest.Count, isoFilesToTest.Count);
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
        LogMessage($"Starting test... Total .iso files to test: {isoFilesToTest.Count}.");

        foreach (var isoFilePath in isoFilesToTest)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var isoFileName = Path.GetFileName(isoFilePath);

            LogMessage($"Testing ISO: {isoFileName}...");
            var testStatus = await TestSingleIsoAsync(extractXisoPath, isoFilePath);
            actualIsosProcessedForProgress++;

            if (testStatus == IsoTestResultStatus.Passed)
            {
                overallIsosTestPassed++;
                _uiSuccessCount++;
            }
            else
            {
                overallIsosTestFailed++;
                _uiFailedCount++;
                failedIsoOriginalPaths.Add(isoFilePath); // Add original path for summary

                // Attempt to move the failed ISO
                var originalDirectory = Path.GetDirectoryName(isoFilePath);
                if (originalDirectory != null)
                {
                    var failedSubfolderPath = Path.Combine(originalDirectory, "Failed");
                    var destinationFailedIsoPath = Path.Combine(failedSubfolderPath, isoFileName);

                    try
                    {
                        await Task.Run(() => Directory.CreateDirectory(failedSubfolderPath), _cts.Token);
                        _cts.Token.ThrowIfCancellationRequested(); // Check before moving

                        if (await Task.Run(() => File.Exists(isoFilePath), _cts.Token)) // Check if source still exists
                        {
                            await Task.Run(() => File.Move(isoFilePath, destinationFailedIsoPath, true), _cts.Token); // Overwrite if already exists in Failed
                            LogMessage($"  Moved failed ISO '{isoFileName}' to '{failedSubfolderPath}'.");
                        }
                        else
                        {
                            LogMessage($"  Source ISO '{isoFileName}' no longer found at original path. Cannot move.");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        LogMessage($"  Move operation for failed ISO '{isoFileName}' was canceled.");
                        // No need to rethrow, main loop will catch cancellation.
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"  Error moving failed ISO '{isoFileName}' to '{failedSubfolderPath}': {ex.Message}");
                        _ = ReportBugAsync($"Error moving failed ISO {isoFileName}", ex);
                    }
                }
            }

            UpdateSummaryStatsUi();
            ProgressBar.Value = actualIsosProcessedForProgress;
        }

        if (!_cts.Token.IsCancellationRequested && actualIsosProcessedForProgress >= ProgressBar.Maximum)
        {
            ProgressBar.Value = ProgressBar.Maximum;
        }

        LogMessage("\nBatch ISO test summary:");
        LogMessage($"ISOs passed test (extractable): {overallIsosTestPassed}");
        LogMessage($"ISOs failed test (not extractable/moved): {overallIsosTestFailed}");

        if (failedIsoOriginalPaths.Count > 0)
        {
            LogMessage("\nList of ISOs that failed the test (original names):");
            foreach (var originalPath in failedIsoOriginalPaths)
            {
                LogMessage($"- {Path.GetFileName(originalPath)}");
            }

            LogMessage("Note: Failed ISOs were attempted to be moved to a 'Failed' subfolder in their original directory.");
        }

        if (overallIsosTestFailed > 0)
        {
            LogMessage("Failed ISOs may be corrupted or not valid Xbox ISO images. Check individual logs for details from extract-xiso.");
        }

        ShowMessageBox($"Batch ISO test completed.\n\n" +
                       $"Passed: {overallIsosTestPassed} ISO files\n" +
                       $"Failed (and moved if possible): {overallIsosTestFailed} ISO files",
            "Test Complete", MessageBoxButton.OK,
            overallIsosTestFailed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private async Task<IsoTestResultStatus> TestSingleIsoAsync(string extractXisoPath, string isoFilePath)
    {
        var isoFileName = Path.GetFileName(isoFilePath);
        var tempExtractionDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_TestExtract", Guid.NewGuid().ToString());

        try
        {
            await Task.Run(() => Directory.CreateDirectory(tempExtractionDir), _cts.Token);
            LogMessage($"  Comprehensive Test for '{isoFileName}'");

            if (!await Task.Run(() => File.Exists(isoFilePath), _cts.Token))
            {
                LogMessage($"  ERROR: ISO file does not exist: {isoFilePath}");
                return IsoTestResultStatus.Failed;
            }

            try
            {
                var fileInfo = new FileInfo(isoFilePath);
                var length = await Task.Run(() => fileInfo.Length, _cts.Token);
                if (length == 0)
                {
                    LogMessage($"  ERROR: ISO file is empty: {isoFileName}");
                    return IsoTestResultStatus.Failed;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogMessage($"  ERROR: Cannot open or check ISO file: {ex.Message}");
                _ = ReportBugAsync($"Error checking file {isoFileName} in TestSingleIsoAsync", ex);
                return IsoTestResultStatus.Failed;
            }

            var extractionSuccess = await RunIsoExtractionToTempAsync(extractXisoPath, isoFilePath, tempExtractionDir);

            if (extractionSuccess)
            {
                LogMessage($"  SUCCESS: '{isoFileName}' extracted successfully for test.");
                return IsoTestResultStatus.Passed;
            }
            else
            {
                LogMessage($"  FAILURE: '{isoFileName}' failed comprehensive extraction test.");
                return IsoTestResultStatus.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage($"  Test for '{isoFileName}' was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"  Unexpected error testing '{isoFileName}': {ex.Message}");
            _ = ReportBugAsync($"Unexpected error in TestSingleIsoAsync for {isoFileName}", ex);
            return IsoTestResultStatus.Failed;
        }
        finally
        {
            try
            {
                if (await Task.Run(() => Directory.Exists(tempExtractionDir), CancellationToken.None))
                {
                    await Task.Run(() => Directory.Delete(tempExtractionDir, true), CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"  Error cleaning temp folder for '{isoFileName}': {ex.Message}");
            }
        }
    }

    private async Task DiagnoseExtractXisoAsync(string extractXisoPath)
    {
        var diagOutputCollector = new StringBuilder();
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = extractXisoPath,
                Arguments = "-h",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(extractXisoPath) ?? AppDomain.CurrentDomain.BaseDirectory
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(_cts.Token);
            var errorOutput = await process.StandardError.ReadToEndAsync(_cts.Token);
            await process.WaitForExitAsync(_cts.Token);

            diagOutputCollector.AppendLine("Extract-xiso Diagnostic Information (STDOUT):");
            diagOutputCollector.AppendLine(string.IsNullOrWhiteSpace(output) ? "(No STDOUT)" : output);

            if (!string.IsNullOrWhiteSpace(errorOutput))
            {
                diagOutputCollector.AppendLine("Extract-xiso Diagnostic Information (STDERR):");
                diagOutputCollector.AppendLine(errorOutput);
            }

            LogMessage(diagOutputCollector.ToString());
        }
        catch (OperationCanceledException)
        {
            LogMessage("extract-xiso diagnostic was canceled.");
        }
        catch (Exception ex)
        {
            LogMessage($"Error running extract-xiso diagnostic: {ex.Message}\n{diagOutputCollector}");
            _ = ReportBugAsync("Error running extract-xiso diagnostic for -h command.", ex);
        }
    }

    private async Task<bool> RunIsoExtractionToTempAsync(string extractXisoPath, string inputFile, string tempExtractionDir)
    {
        var isoFileName = Path.GetFileName(inputFile);
        LogMessage($"    Detailed Extraction Attempt for: {isoFileName}");

        var processOutputCollector = new StringBuilder();
        Process? processRef;
        CancellationTokenRegistration cancellationRegistration = default;

        try
        {
            Directory.CreateDirectory(tempExtractionDir); // Synchronous is fine for local temp path creation

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

            cancellationRegistration = _cts.Token.Register(() =>
            {
                try
                {
                    processRef?.Kill(true);
                }
                catch
                {
                    /* Ignore */
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

            await process.WaitForExitAsync(_cts.Token);
            _cts.Token.ThrowIfCancellationRequested();

            var collectedOutput = processOutputCollector.ToString();
            LogMessage($"    Process Exit Code for '{isoFileName}': {process.ExitCode}");
            LogMessage($"    Full Output Log from extract-xiso -x for '{isoFileName}':\n{collectedOutput}");

            var isoNameWithoutExtension = Path.GetFileNameWithoutExtension(isoFileName);
            var expectedExtractionSubDir = Path.Combine(tempExtractionDir, isoNameWithoutExtension);

            var filesWereExtracted = false;
            if (await Task.Run(() => Directory.Exists(expectedExtractionSubDir), _cts.Token))
            {
                var extractedFiles = await Task.Run(() => Directory.GetFiles(expectedExtractionSubDir, "*", SearchOption.AllDirectories), _cts.Token);
                filesWereExtracted = extractedFiles.Length > 0;
                LogMessage($"    Files found by Directory.GetFiles in '{expectedExtractionSubDir}' (recursive): {extractedFiles.Length}. filesWereExtracted: {filesWereExtracted}");
            }
            else
            {
                LogMessage($"    Expected extraction subdirectory '{expectedExtractionSubDir}' not found.");
            }

            var summaryLinePresent = collectedOutput.Contains("files in") && collectedOutput.Contains("total") && collectedOutput.Contains("bytes");

            if (process.ExitCode == 0)
            {
                if (!filesWereExtracted)
                {
                    LogMessage("    extract-xiso process exited with 0, but no files were extracted. Considered a failure.");
                    return false;
                }

                var criticalErrorInStdErr = collectedOutput.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => line.StartsWith("STDERR:", StringComparison.OrdinalIgnoreCase) &&
                                 (line.Contains("failed to extract", StringComparison.OrdinalIgnoreCase) ||
                                  line.Contains("error extracting", StringComparison.OrdinalIgnoreCase) ||
                                  line.Contains("cannot open", StringComparison.OrdinalIgnoreCase) ||
                                  line.Contains("not a valid", StringComparison.OrdinalIgnoreCase)));
                if (criticalErrorInStdErr)
                {
                    LogMessage("    extract-xiso process exited with 0 and files were extracted, but critical error messages were found in STDERR. Considered a failure.");
                    return false;
                }

                LogMessage("    extract-xiso process completed successfully (ExitCode 0, files extracted, no critical errors in log).");
                return true;
            }
            else if (process.ExitCode == 1)
            {
                var stdErrLines = collectedOutput.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(static line => line.StartsWith("STDERR:", StringComparison.OrdinalIgnoreCase))
                    .Select(static line => line.Substring("STDERR:".Length).Trim())
                    .ToArray();

                var onlyKnownBenignErrors = stdErrLines.All(errLine =>
                    errLine.Contains("open error: -d No such file or directory") || // Should be gone, but defensive
                    errLine.Contains("open error: LoadScreen_BlackScreen.nif No such file or directory") ||
                    errLine.Contains("failed to extract xbox iso image")
                );

                if (filesWereExtracted && summaryLinePresent && (stdErrLines.Length == 0 || onlyKnownBenignErrors))
                {
                    LogMessage("    extract-xiso process exited with 1, but files were extracted, summary line present, and STDERR contained only known benign issues (or was empty). Considered a pass for testing.");
                    return true;
                }
                else
                {
                    LogMessage($"    extract-xiso process finished with exit code 1. Files extracted: {filesWereExtracted}. Summary line: {summaryLinePresent}. STDERR lines: {string.Join("; ", stdErrLines)}. Considered a failure.");
                    return false;
                }
            }
            else
            {
                LogMessage($"    extract-xiso process finished with non-zero exit code: {process.ExitCode}. Considered a failure.");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage($"    Extraction for {isoFileName} was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"    Critical Error during extraction of {isoFileName}: {ex.Message}");
            _ = ReportBugAsync($"Critical error during RunIsoExtractionToTempAsync for {isoFileName}", ex);
            return false;
        }
        finally
        {
            await cancellationRegistration.DisposeAsync();
        }
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
                LogMessage($"{logPrefix} extract-xiso tool reported failure.");
                return FileProcessingStatus.Failed;
            }

            await Task.Run(() => Directory.CreateDirectory(outputFolder), _cts.Token);
            var destinationPath = Path.Combine(outputFolder, fileName);

            if (toolResult == ConversionToolResultStatus.Skipped)
            {
                LogMessage($"{logPrefix} Already optimized. Copying to output.");
                await Task.Run(() => File.Copy(inputFile, destinationPath, true), _cts.Token);

                if (!deleteOriginalIsoFile ||
                    inputFile.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                    return FileProcessingStatus.Skipped;

                LogMessage($"{logPrefix} Deleting original (skipped) file: {fileName}");
                await Task.Run(() => File.Delete(inputFile), _cts.Token);
                return FileProcessingStatus.Skipped;
            }

            var convertedFilePath = inputFile;
            var originalBackupPath = inputFile + ".old";

            LogMessage($"{logPrefix} Moving converted file to output folder: {destinationPath}");
            await Task.Run(() => File.Move(convertedFilePath, destinationPath, true), _cts.Token);

            var isTemporaryFileFromArchive = inputFile.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

            if (await Task.Run(() => File.Exists(originalBackupPath), _cts.Token))
            {
                if (deleteOriginalIsoFile && !isTemporaryFileFromArchive)
                {
                    LogMessage($"{logPrefix} Deleting original backup file: {Path.GetFileName(originalBackupPath)}");
                    await Task.Run(() => File.Delete(originalBackupPath), _cts.Token);
                }
                else if (!isTemporaryFileFromArchive)
                {
                    LogMessage($"{logPrefix} Restoring original file from backup: {fileName}");
                    await Task.Run(() => File.Move(originalBackupPath, inputFile, true), _cts.Token);
                }
                else
                {
                    LogMessage($"{logPrefix} Original backup file {Path.GetFileName(originalBackupPath)} for temporary ISO will be cleaned with temp folder.");
                }
            }
            else if (deleteOriginalIsoFile && !isTemporaryFileFromArchive)
            {
                LogMessage($"{logPrefix} Converted file moved. Original (in-place converted) is now at destination. No separate .old file to delete for source path.");
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
        var isoFileName = Path.GetFileName(inputFile);
        LogMessage($"Running extract-xiso -r on: {isoFileName}");

        var localProcessOutputLines = new List<string>();
        var outputForUiLog = new StringBuilder();

        Process? processRef;
        CancellationTokenRegistration cancellationRegistration = default;

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

            cancellationRegistration = _cts.Token.Register(() =>
            {
                try
                {
                    processRef?.Kill(true);
                }
                catch
                {
                    /* Ignore */
                }
            });

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null) return;

                lock (localProcessOutputLines)
                {
                    localProcessOutputLines.Add(args.Data);
                }

                lock (outputForUiLog)
                {
                    outputForUiLog.AppendLine(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrEmpty(args.Data)) return;

                lock (localProcessOutputLines)
                {
                    localProcessOutputLines.Add($"ERROR: {args.Data}");
                }

                lock (outputForUiLog)
                {
                    outputForUiLog.AppendLine(CultureInfo.InvariantCulture, $"extract-xiso error: {args.Data}");
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(_cts.Token);
            _cts.Token.ThrowIfCancellationRequested();

            var collectedToolOutputForLog = outputForUiLog.ToString();
            if (!string.IsNullOrWhiteSpace(collectedToolOutputForLog))
            {
                LogMessage($"Output from extract-xiso -r for {isoFileName}:\n{collectedToolOutputForLog}");
            }

            switch (process.ExitCode)
            {
                case 0 when localProcessOutputLines.Any(static line => line.Contains("is already optimized, skipping...", StringComparison.OrdinalIgnoreCase) ||
                                                                       line.Contains("already an XISO image", StringComparison.OrdinalIgnoreCase)):
                    return ConversionToolResultStatus.Skipped;
                case 0:
                    return ConversionToolResultStatus.Success;
                case 1 when localProcessOutputLines.Any(static line => line.Contains("is already optimized, skipping...", StringComparison.OrdinalIgnoreCase) ||
                                                                       line.Contains("already an XISO image", StringComparison.OrdinalIgnoreCase)):
                    return ConversionToolResultStatus.Skipped;
                case 1:
                    LogMessage($"extract-xiso -r for {isoFileName} exited with 1 but no 'skipped' message. Treating as failure.");
                    _ = ReportBugAsync($"extract-xiso -r for {isoFileName} exited 1 without skip message. Output: {string.Join(Environment.NewLine, localProcessOutputLines)}");
                    return ConversionToolResultStatus.Failed;
                default:
                    _ = ReportBugAsync($"extract-xiso -r failed for {isoFileName} with exit code {process.ExitCode}. Output: {string.Join(Environment.NewLine, localProcessOutputLines)}");
                    return ConversionToolResultStatus.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage($"extract-xiso -r operation for {isoFileName} was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error running extract-xiso -r for {isoFileName}: {ex.Message}");
            _ = ReportBugAsync($"Exception during extract-xiso -r for {isoFileName}", ex);
            return ConversionToolResultStatus.Failed;
        }
        finally
        {
            await cancellationRegistration.DisposeAsync();
        }
    }

    private async Task<bool> ExtractArchiveAsync(string sevenZipPath, string archivePath, string extractionPath)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        LogMessage($"Extracting: {archiveFileName} using 7z.exe to {extractionPath}");

        var processOutputCollector = new StringBuilder();
        Process? processRef;
        CancellationTokenRegistration cancellationRegistration = default;

        try
        {
            using var process = new Process();
            processRef = process;
            process.StartInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments = $"x \"{archivePath}\" -o\"{extractionPath}\" -y -bso0 -bsp0",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };

            cancellationRegistration = _cts.Token.Register(() =>
            {
                try
                {
                    processRef?.Kill(true);
                }
                catch
                {
                    /* Ignore */
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
                    processOutputCollector.AppendLine(CultureInfo.InvariantCulture, $"7z error: {args.Data}");
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(_cts.Token);
            _cts.Token.ThrowIfCancellationRequested();

            var collectedOutput = processOutputCollector.ToString();
            if (!string.IsNullOrWhiteSpace(collectedOutput))
            {
                LogMessage($"Output from 7z.exe for {archiveFileName}:\n{collectedOutput}");
            }

            if (process.ExitCode == 0)
            {
                LogMessage($"Successfully extracted: {archiveFileName}");
                return true;
            }
            else
            {
                LogMessage($"7z.exe failed to extract {archiveFileName}. Exit Code: {process.ExitCode}.");
                _ = ReportBugAsync($"7z.exe failed for {archiveFileName}. Exit: {process.ExitCode}", new Exception($"7z Output: {collectedOutput}"));
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage($"Extraction of {archiveFileName} was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Exception during extraction of {archiveFileName}: {ex.Message}");
            _ = ReportBugAsync($"Exception extracting {archiveFileName}", ex);
            return false;
        }
        finally
        {
            await cancellationRegistration.DisposeAsync();
        }
    }

    private async Task CleanupTempFoldersAsync(List<string> tempFolders)
    {
        if (tempFolders.Count == 0) return;

        LogMessage("Cleaning up remaining temporary extraction folders...");
        foreach (var folder in tempFolders.ToList())
        {
            try
            {
                if (!await Task.Run(() => Directory.Exists(folder), CancellationToken.None)) continue;

                await Task.Run(() => Directory.Delete(folder, true), CancellationToken.None);
                tempFolders.Remove(folder);
            }
            catch (Exception ex)
            {
                LogMessage($"Error cleaning temp folder {folder}: {ex.Message}");
            }
        }

        if (tempFolders.Count == 0) LogMessage("Temporary folder cleanup complete.");
        else LogMessage("Some temporary folders could not be cleaned automatically.");
    }

    private async Task TryDeleteFileAsync(string filePath)
    {
        try
        {
            if (!await Task.Run(() => File.Exists(filePath), _cts.Token)) return;

            await Task.Run(() => File.Delete(filePath), _cts.Token);
            LogMessage($"Deleted: {Path.GetFileName(filePath)}");
        }
        catch (OperationCanceledException)
        {
            /* Ignore */
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
                AppendExceptionDetails(fullReport, exception);
            }

            if (LogViewer != null)
            {
                var logContent = string.Empty;
                await Dispatcher.InvokeAsync(() => { logContent = LogViewer.Text; });
                if (!string.IsNullOrEmpty(logContent))
                {
                    fullReport.AppendLine();
                    fullReport.AppendLine("=== Application Log (last part) ===");
                    const int maxLogLength = 10000;
                    var start = Math.Max(0, logContent.Length - maxLogLength);
                    fullReport.Append(logContent.AsSpan(start));
                }
            }

            await _bugReportService.SendBugReportAsync(fullReport.ToString());
        }
        catch
        {
            // ignored
        }
    }

    private static void AppendExceptionDetails(StringBuilder sb, Exception exception, int level = 0)
    {
        while (true)
        {
            var indent = new string(' ', level * 2);

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Type: {exception.GetType().FullName}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Message: {exception.Message}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Source: {exception.Source}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}StackTrace:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{exception.StackTrace}");

            if (exception.InnerException != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Inner Exception:");
                exception = exception.InnerException;
                level += 1;
                continue;
            }

            break;
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