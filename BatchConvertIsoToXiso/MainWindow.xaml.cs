using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Threading;
using System.Windows.Controls;
using SevenZip;

namespace BatchConvertIsoToXiso;

public partial class MainWindow : IDisposable
{
    private CancellationTokenSource _cts;
    private readonly BugReportService _bugReportService;
    private readonly UpdateChecker _updateChecker;

    // Bug Report API configuration
    private const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string BugReportApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string ApplicationName = "BatchConvertIsoToXiso";

    // Summary Stats
    private DateTime _operationStartTime;
    private readonly DispatcherTimer _processingTimer;
    private int _uiTotalFiles;
    private int _uiSuccessCount;
    private int _uiFailedCount;
    private int _uiSkippedCount;

    // Performance Counter for Disk Write Speed
    private PerformanceCounter? _diskWriteSpeedCounter;
    private string? _activeMonitoringDriveLetter;
    private string? _currentOperationDrive;

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
        _updateChecker = new UpdateChecker();

        _processingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _processingTimer.Tick += ProcessingTimer_Tick;
        ResetSummaryStats();

        DisplayInitialInstructions();

        Loaded += async (s, e) => await CheckForUpdatesAsync();
    }

    private void DisplayInitialInstructions()
    {
        LogMessage("Welcome to the Batch Convert ISO to XISO & Test Tool.");
        LogMessage("");
        LogMessage("This application provides two main functions, available in the tabs above:");
        LogMessage("1. Convert to XISO: Converts standard Xbox ISO files to the optimized XISO format. It can also process ISOs found within .zip, .7z, and .rar archives.");
        LogMessage("2. Test ISO Integrity: Verifies the integrity of your .iso files by attempting a full extraction to a temporary location.");
        LogMessage("");
        LogMessage("General Steps:");
        LogMessage("- Select the appropriate tab for the operation you want to perform.");
        LogMessage("- Use the 'Browse' buttons to select your source and destination folders.");
        LogMessage("- Configure the options for your chosen operation.");
        LogMessage("- Click the 'Start' button to begin.");
        LogMessage("");

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");
        if (File.Exists(extractXisoPath))
        {
            LogMessage("INFO: extract-xiso.exe found in the application directory.");
        }
        else
        {
            LogMessage("WARNING: extract-xiso.exe not found. ISO conversion and testing will fail.");
            Task.Run(() => Task.FromResult(_ = ReportBugAsync("extract-xiso.exe not found.")));
        }

        LogMessage("INFO: Archive extraction uses the SevenZipExtractor library.");
        LogMessage("--- Ready ---");
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The log is no longer cleared or updated when switching tabs to preserve the initial instructions.
        if (e.Source is not TabControl) return;
    }

    private string? GetDriveLetter(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            // Ensure the path is absolute to get a root.
            var fullPath = Path.GetFullPath(path);
            var pathRoot = Path.GetPathRoot(fullPath);

            if (string.IsNullOrEmpty(pathRoot)) return null;

            var driveInfo = new DriveInfo(pathRoot);
            // driveInfo.Name will be like "C:\\" for local drives.
            // We need "C:" for the performance counter instance name.
            return driveInfo.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException) // Handles invalid paths, UNC paths for which DriveInfo might fail
        {
            LogMessage($"Could not determine drive letter for path: {path}. It might be a network path or invalid.");
            return null;
        }
        catch (Exception ex) // Catch other potential exceptions
        {
            LogMessage($"Error getting drive letter for path {path}: {ex.Message}");
            return null;
        }
    }

    private static string FormatBytesPerSecond(float bytesPerSecond)
    {
        const int kilobyte = 1024;
        const int megabyte = kilobyte * 1024;

        if (bytesPerSecond < kilobyte)
            return $"{bytesPerSecond:F1} B/s";
        if (bytesPerSecond < megabyte)
            return $"{bytesPerSecond / kilobyte:F1} KB/s";

        return $"{bytesPerSecond / megabyte:F1} MB/s";
    }

    private void SetCurrentOperationDrive(string? driveLetter)
    {
        if (_currentOperationDrive == driveLetter) return;

        _currentOperationDrive = driveLetter;

        // Switch performance counter to the new drive
        if (!string.IsNullOrEmpty(driveLetter) && _processingTimer.IsEnabled)
        {
            InitializePerformanceCounter(driveLetter);
        }
    }

    private void InitializePerformanceCounter(string? driveLetter)
    {
        StopPerformanceCounter(); // Stop any existing counter

        if (string.IsNullOrEmpty(driveLetter))
        {
            LogMessage("Cannot monitor write speed: Drive letter is invalid or not determined (e.g., network path).");
            Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A");
            return;
        }

        var perfCounterInstanceName = driveLetter.EndsWith(':') ? driveLetter : driveLetter + ":";

        try
        {
            // First, check if the category exists. If not, we can't proceed.
            if (!PerformanceCounterCategory.Exists("LogicalDisk"))
            {
                LogMessage($"Performance counter category 'LogicalDisk' does not exist. Cannot monitor write speed for drive {perfCounterInstanceName}.");
                Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A (Category Missing)");
                return;
            }

            // Now, check if the specific instance exists for the given drive letter.
            if (!PerformanceCounterCategory.InstanceExists(perfCounterInstanceName, "LogicalDisk"))
            {
                LogMessage($"Performance counter instance '{perfCounterInstanceName}' not found for 'LogicalDisk'. Cannot monitor write speed for this drive.");
                Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A (Instance Missing)");
                return;
            }

            // If both category and instance exist, proceed with creating the counter.
            _diskWriteSpeedCounter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", perfCounterInstanceName, true);
            _diskWriteSpeedCounter.NextValue(); // Initial call to prime the counter
            _activeMonitoringDriveLetter = driveLetter;
            LogMessage($"Monitoring write speed for drive: {perfCounterInstanceName}");
            Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "Calculating...");
        }
        catch (InvalidOperationException ex)
        {
            // This catch block should now primarily handle issues during counter creation/access after existence checks.
            LogMessage($"Error initializing performance counter for drive {perfCounterInstanceName}: {ex.Message}. Write speed monitoring disabled.");
            _ = ReportBugAsync($"PerfCounter Init InvalidOpExc for {perfCounterInstanceName}", ex);
            _diskWriteSpeedCounter?.Dispose();
            _diskWriteSpeedCounter = null;
            _activeMonitoringDriveLetter = null;
            Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A (Error)");
        }
        catch (Exception ex)
        {
            // Catch any other unexpected exceptions during initialization.
            LogMessage($"Unexpected error initializing performance counter for drive {perfCounterInstanceName}: {ex.Message}. Write speed monitoring disabled.");
            _ = ReportBugAsync($"PerfCounter Init GenericExc for {perfCounterInstanceName}", ex);
            _diskWriteSpeedCounter?.Dispose();
            _diskWriteSpeedCounter = null;
            _activeMonitoringDriveLetter = null;
            Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A (Error)");
        }
    }

    private void StopPerformanceCounter()
    {
        _diskWriteSpeedCounter?.Dispose();
        _diskWriteSpeedCounter = null;
        _activeMonitoringDriveLetter = null;
        // Ensure UI update happens on the UI thread
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (WriteSpeedValue != null)
            {
                WriteSpeedValue.Text = "N/A";
            }
        });
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

    private void BrowseConversionInputButton_Click(object sender, RoutedEventArgs e)
    {
        var inputFolder = SelectFolder("Select the folder containing ISO or archive files");
        if (string.IsNullOrEmpty(inputFolder)) return;

        ConversionInputFolderTextBox.Text = inputFolder;
        LogMessage($"Conversion input folder selected: {inputFolder}");
    }

    private void BrowseConversionOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var outputFolder = SelectFolder("Select the output folder for converted XISO files");
        if (string.IsNullOrEmpty(outputFolder)) return;

        ConversionOutputFolderTextBox.Text = outputFolder;
        LogMessage($"Conversion output folder selected: {outputFolder}");
    }

    private void BrowseTestInputButton_Click(object sender, RoutedEventArgs e)
    {
        var inputFolder = SelectFolder("Select the folder containing ISO files to test");
        if (string.IsNullOrEmpty(inputFolder)) return;

        TestInputFolderTextBox.Text = inputFolder;
        LogMessage($"Test input folder selected: {inputFolder}");
    }

    private async void StartConversionButton_Click(object sender, RoutedEventArgs e)
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
                _ = ReportBugAsync("extract-xiso.exe not found at start of conversion.", new FileNotFoundException("extract-xiso.exe missing", extractXisoPath));
                StopPerformanceCounter();
                return;
            }

            var inputFolder = ConversionInputFolderTextBox.Text;
            var outputFolder = ConversionOutputFolderTextBox.Text;
            var deleteFiles = DeleteOriginalsCheckBox.IsChecked ?? false;

            if (string.IsNullOrEmpty(inputFolder) || string.IsNullOrEmpty(outputFolder))
            {
                ShowError("Please select both input and output folders for conversion.");
                StopPerformanceCounter();
                return;
            }

            if (inputFolder.Equals(outputFolder, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Input and output folders must be different for conversion.");
                StopPerformanceCounter();
                return;
            }

            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }

            ResetSummaryStats();

            // Start with output drive but allow dynamic switching
            var outputDrive = GetDriveLetter(outputFolder);
            _currentOperationDrive = outputDrive;
            InitializePerformanceCounter(outputDrive);

            _operationStartTime = DateTime.Now;
            _processingTimer.Start();

            SetControlsState(false);
            UpdateStatus("Starting batch conversion...");
            LogMessage("--- Starting batch conversion process... ---");
            LogMessage($"Input folder: {inputFolder}");
            LogMessage($"Output folder: {outputFolder}");
            LogMessage($"Delete original files: {deleteFiles}");

            try
            {
                await PerformBatchConversionAsync(extractXisoPath, inputFolder, outputFolder, deleteFiles);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Operation was canceled by user.");
                UpdateStatus("Operation cancelled. Ready.");
            }
            catch (Exception ex)
            {
                LogMessage($"Critical Error: {ex.Message}");
                UpdateStatus("An error occurred. Ready.");
                _ = ReportBugAsync("Critical error during batch conversion process", ex);
            }
            finally
            {
                _processingTimer.Stop();
                StopPerformanceCounter();
                _currentOperationDrive = null; // Reset
                var finalElapsedTime = DateTime.Now - _operationStartTime;
                ProcessingTimeValue.Text = finalElapsedTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                SetControlsState(true);
                LogOperationSummary("Conversion");
                UpdateStatus("Conversion complete. Ready.");
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync($"Error during batch conversion process: {ex.Message}", ex);
            LogMessage($"Error during batch conversion process: {ex.Message}");
            StopPerformanceCounter();
        }
    }

    private async void StartTestButton_Click(object sender, RoutedEventArgs e)
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
                StopPerformanceCounter();
                return;
            }

            var inputFolder = TestInputFolderTextBox.Text;
            var moveSuccessful = MoveSuccessFilesCheckBox.IsChecked == true;
            var successFolder = Path.Combine(inputFolder, "_success");
            var moveFailed = MoveFailedFilesCheckBox.IsChecked == true;
            var failedFolder = Path.Combine(inputFolder, "_failed");

            if (string.IsNullOrEmpty(inputFolder))
            {
                ShowError("Please select the input folder for testing.");
                StopPerformanceCounter();
                return;
            }

            if (moveSuccessful && string.IsNullOrEmpty(successFolder))
            {
                ShowError("Please select a Success Folder or uncheck the option to move successful files.");
                StopPerformanceCounter();
                return;
            }

            if (moveFailed && string.IsNullOrEmpty(failedFolder))
            {
                ShowError("Please select a Failed Folder or uncheck the option to move failed files.");
                StopPerformanceCounter();
                return;
            }

            if (moveSuccessful && moveFailed && !string.IsNullOrEmpty(successFolder) && successFolder.Equals(failedFolder, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Success Folder and Failed Folder cannot be the same.");
                StopPerformanceCounter();
                return;
            }

            if ((moveSuccessful && !string.IsNullOrEmpty(successFolder) && successFolder.Equals(inputFolder, StringComparison.OrdinalIgnoreCase)) ||
                (moveFailed && !string.IsNullOrEmpty(failedFolder) && failedFolder.Equals(inputFolder, StringComparison.OrdinalIgnoreCase)))
            {
                ShowError("Success/Failed folder cannot be the same as the Input folder.");
                StopPerformanceCounter();
                return;
            }

            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }

            ResetSummaryStats();

            // Writes occur in the system's temporary directory for testing, or output for successful moves
            var initialDriveForMonitoring = moveSuccessful ? GetDriveLetter(successFolder) : GetDriveLetter(Path.GetTempPath());
            _currentOperationDrive = initialDriveForMonitoring; // Initial drive
            InitializePerformanceCounter(initialDriveForMonitoring);

            _operationStartTime = DateTime.Now;
            _processingTimer.Start();

            SetControlsState(false);
            UpdateStatus("Starting batch ISO test...");
            LogMessage("--- Starting batch ISO test process... ---");
            LogMessage($"Input folder: {inputFolder}");
            if (moveSuccessful) LogMessage($"Moving successful files to: {successFolder}");
            if (moveFailed) LogMessage($"Moving failed files to: {failedFolder}");

            try
            {
                await PerformBatchIsoTestAsync(extractXisoPath, inputFolder, moveSuccessful, successFolder, moveFailed, failedFolder);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Operation was canceled by user.");
                UpdateStatus("Operation cancelled. Ready.");
            }
            catch (Exception ex)
            {
                LogMessage($"Critical Error during ISO test: {ex.Message}");
                UpdateStatus("An error occurred. Ready.");
                _ = ReportBugAsync("Critical error during batch ISO test process", ex);
            }
            finally
            {
                _processingTimer.Stop();
                StopPerformanceCounter();
                _currentOperationDrive = null; // Reset
                var finalElapsedTime = DateTime.Now - _operationStartTime;
                ProcessingTimeValue.Text = finalElapsedTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                SetControlsState(true);
                LogOperationSummary("Test");
                UpdateStatus("Test complete. Ready.");
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync($"Error during batch ISO test process: {ex.Message}", ex);
            LogMessage($"Error during batch ISO test process: {ex.Message}");
            StopPerformanceCounter();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        LogMessage("Cancellation requested. Finishing current file/archive...");
    }

    private void SetControlsState(bool enabled)
    {
        // Conversion Tab
        ConversionInputFolderTextBox.IsEnabled = enabled;
        BrowseConversionInputButton.IsEnabled = enabled;
        ConversionOutputFolderTextBox.IsEnabled = enabled;
        BrowseConversionOutputButton.IsEnabled = enabled;
        DeleteOriginalsCheckBox.IsEnabled = enabled;
        StartConversionButton.IsEnabled = enabled;

        // Test Tab
        TestInputFolderTextBox.IsEnabled = enabled;
        BrowseTestInputButton.IsEnabled = enabled;
        MoveSuccessFilesCheckBox.IsEnabled = enabled;
        MoveFailedFilesCheckBox.IsEnabled = enabled;
        StartTestButton.IsEnabled = enabled;

        SuccessFolderPanel.IsEnabled = enabled;
        FailedFolderPanel.IsEnabled = enabled;

        // Main Window Controls
        MainTabControl.IsEnabled = enabled;
        ProgressBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

        if (!enabled) return;

        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
        if (ProgressTextBlock != null)
        {
            ProgressTextBlock.Text = "";
        }
    }

    private void MoveSuccessFilesCheckBox_CheckedUnchecked(object sender, RoutedEventArgs e)
    {
        if (SuccessFolderPanel == null)
        {
            return; // Control is not yet initialized, do nothing.
        }

        SuccessFolderPanel.Visibility = MoveSuccessFilesCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SetControlsState(StartConversionButton.IsEnabled);
    }

    private void MoveFailedFilesCheckBox_CheckedUnchecked(object sender, RoutedEventArgs e)
    {
        if (FailedFolderPanel == null)
        {
            return; // Control is not yet initialized, do nothing.
        }

        FailedFolderPanel.Visibility = MoveFailedFilesCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SetControlsState(StartConversionButton.IsEnabled);
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
        _uiSkippedCount = 0;
        UpdateSummaryStatsUi();
        UpdateProgressUi(0, 0);
        ProcessingTimeValue.Text = "00:00:00";
        if (!_processingTimer.IsEnabled)
        {
            StopPerformanceCounter();
        }
    }

    private void UpdateSummaryStatsUi(int? newTotalFiles = null)
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
            SkippedValue.Text = _uiSkippedCount.ToString(CultureInfo.InvariantCulture);
        });
    }

    private void ProcessingTimer_Tick(object? sender, EventArgs e)
    {
        var elapsedTime = DateTime.Now - _operationStartTime;
        ProcessingTimeValue.Text = elapsedTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        if (_diskWriteSpeedCounter == null || string.IsNullOrEmpty(_activeMonitoringDriveLetter)) return;

        try
        {
            var writeSpeedBytes = _diskWriteSpeedCounter.NextValue();
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (WriteSpeedValue != null)
                {
                    WriteSpeedValue.Text = FormatBytesPerSecond(writeSpeedBytes);
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            LogMessage($"Error reading write speed for drive {_activeMonitoringDriveLetter}: {ex.Message}. Stopping monitoring.");
            _ = ReportBugAsync($"PerfCounter Read InvalidOpExc for {_activeMonitoringDriveLetter}", ex);
            StopPerformanceCounter();
        }
        catch (Exception ex)
        {
            LogMessage($"Unexpected error reading write speed for drive {_activeMonitoringDriveLetter}: {ex.Message}. Stopping monitoring.");
            _ = ReportBugAsync($"PerfCounter Read GenericExc for {_activeMonitoringDriveLetter}", ex);
            StopPerformanceCounter();
        }
    }

    private void UpdateStatus(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (StatusTextBlock != null)
            {
                StatusTextBlock.Text = message;
            }
        });
    }

    private void UpdateProgressUi(int current, int total)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (ProgressBar == null || ProgressTextBlock == null) return;

            ProgressBar.Maximum = total > 0 ? total : 1;
            ProgressBar.Value = current;

            if (total > 0 && ProgressBar.Visibility == Visibility.Visible)
            {
                var percentage = ((double)current / total) * 100;
                ProgressTextBlock.Text = $"{current} of {total} ({percentage:F0}%)";
            }
            else
            {
                ProgressTextBlock.Text = "";
            }
        });
    }

    // Helper method to generate a simple filename for extract-xiso processing
    private static string GenerateSimpleFilename(int fileIndex)
    {
        return $"iso_{fileIndex:D6}.iso";
    }

    private async Task PerformBatchConversionAsync(string extractXisoPath, string inputFolder, string outputFolder, bool deleteOriginals)
    {
        var tempFoldersToCleanUpAtEnd = new List<string>();
        var archivesFailedToExtractOrProcess = 0;
        var actualIsosProcessedForProgress = 0;
        var failedConversionFilePaths = new List<string>();

        _uiSuccessCount = 0;
        _uiFailedCount = 0;
        _uiSkippedCount = 0;

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
                            return ext is ".iso" or ".zip" or ".7z" or ".rar";
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
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
            return;
        }

        LogMessage($"Found {initialEntriesToProcess.Count} top-level items (ISOs or archives) for conversion.");
        var currentExpectedTotalIsos = initialEntriesToProcess.Count;
        UpdateSummaryStatsUi(currentExpectedTotalIsos);
        UpdateProgressUi(0, currentExpectedTotalIsos);
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
        LogMessage($"Starting conversion... Total items to process initially: {currentExpectedTotalIsos}. This may increase if archives contain multiple ISOs.");

        var globalFileIndex = 1; // Global counter for simple filenames

        foreach (var currentEntryPath in initialEntriesToProcess)
        {
            _cts.Token.ThrowIfCancellationRequested();

            var entryFileName = Path.GetFileName(currentEntryPath);
            UpdateStatus($"Processing: {entryFileName}");
            var entryExtension = Path.GetExtension(currentEntryPath).ToLowerInvariant();

            if (entryExtension == ".iso")
            {
                LogMessage($"Processing standalone ISO: {entryFileName}...");
                SetCurrentOperationDrive(GetDriveLetter(outputFolder));
                var status = await ConvertFileAsync(extractXisoPath, currentEntryPath, outputFolder, deleteOriginals, globalFileIndex);
                globalFileIndex++;
                actualIsosProcessedForProgress++;
                switch (status)
                {
                    case FileProcessingStatus.Converted:
                        _uiSuccessCount++;
                        break;
                    case FileProcessingStatus.Skipped:
                        _uiSkippedCount++;
                        break;
                    case FileProcessingStatus.Failed:
                        _uiFailedCount++;
                        failedConversionFilePaths.Add(currentEntryPath);
                        break;
                }

                UpdateSummaryStatsUi();
                UpdateProgressUi(actualIsosProcessedForProgress, currentExpectedTotalIsos);
            }
            else if (entryExtension is ".zip" or ".7z" or ".rar")
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
                    SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath()));
                    archiveExtractedSuccessfully = await ExtractArchiveAsync(currentEntryPath, currentArchiveTempExtractionDir);
                    if (archiveExtractedSuccessfully)
                    {
                        var extractedIsoFiles = await Task.Run(() => Directory.GetFiles(currentArchiveTempExtractionDir, "*.iso", SearchOption.AllDirectories), _cts.Token);
                        if (extractedIsoFiles.Length > 0)
                        {
                            var newIsosFound = extractedIsoFiles.Length;
                            currentExpectedTotalIsos += (newIsosFound - 1);
                            UpdateSummaryStatsUi(currentExpectedTotalIsos);
                            UpdateProgressUi(actualIsosProcessedForProgress, currentExpectedTotalIsos);
                            LogMessage($"Found {newIsosFound} ISO(s) in {entryFileName}. Total expected ISOs now: {currentExpectedTotalIsos}. Processing them now...");
                        }
                        else if (extractedIsoFiles.Length == 0)
                        {
                            LogMessage($"No ISO files found in archive: {entryFileName}.");
                            actualIsosProcessedForProgress++;
                            UpdateProgressUi(actualIsosProcessedForProgress, currentExpectedTotalIsos);
                        }

                        foreach (var extractedIsoPath in extractedIsoFiles)
                        {
                            _cts.Token.ThrowIfCancellationRequested();
                            var extractedIsoName = Path.GetFileName(extractedIsoPath);
                            LogMessage($"  Converting ISO from archive: {extractedIsoName}...");
                            SetCurrentOperationDrive(GetDriveLetter(outputFolder));
                            var status = await ConvertFileAsync(extractXisoPath, extractedIsoPath, outputFolder, false, globalFileIndex);
                            globalFileIndex++;
                            statusesOfIsosInThisArchive.Add(status);
                            actualIsosProcessedForProgress++;
                            switch (status)
                            {
                                case FileProcessingStatus.Converted:
                                    _uiSuccessCount++;
                                    break;
                                case FileProcessingStatus.Skipped:
                                    _uiSkippedCount++;
                                    break;
                                case FileProcessingStatus.Failed:
                                    _uiFailedCount++;
                                    failedConversionFilePaths.Add(extractedIsoPath);
                                    break;
                            }

                            UpdateSummaryStatsUi();
                            UpdateProgressUi(actualIsosProcessedForProgress, currentExpectedTotalIsos);
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
                        UpdateProgressUi(actualIsosProcessedForProgress, currentExpectedTotalIsos);
                    }

                    if (deleteOriginals && archiveExtractedSuccessfully)
                    {
                        var allIsosFromArchiveOk = statusesOfIsosInThisArchive.Count > 0 &&
                                                   statusesOfIsosInThisArchive.All(static s => s is FileProcessingStatus.Converted or FileProcessingStatus.Skipped);
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
                        UpdateProgressUi(actualIsosProcessedForProgress, currentExpectedTotalIsos);
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

        if (!_cts.Token.IsCancellationRequested && actualIsosProcessedForProgress >= currentExpectedTotalIsos)
        {
            UpdateProgressUi(currentExpectedTotalIsos, currentExpectedTotalIsos);
        }

        if (archivesFailedToExtractOrProcess > 0)
        {
            LogMessage($"Note: {archivesFailedToExtractOrProcess} archive(s) failed to extract or had processing errors.");
        }

        if (failedConversionFilePaths.Count > 0)
        {
            LogMessage("\nList of items that failed conversion or archive extraction:");
            foreach (var failedPath in failedConversionFilePaths)
            {
                LogMessage($"- {Path.GetFileName(failedPath)} (Full path: {failedPath})");
            }
        }

        await CleanupTempFoldersAsync(tempFoldersToCleanUpAtEnd);
    }

    private async Task PerformBatchIsoTestAsync(string extractXisoPath, string inputFolder, bool moveSuccessful, string? successFolder, bool moveFailed, string? failedFolder)
    {
        var actualIsosProcessedForProgress = 0;
        var failedIsoOriginalPaths = new List<string>(); // Keep track of original paths for logging

        _uiSuccessCount = 0;
        _uiFailedCount = 0;
        _uiSkippedCount = 0; // Not used by test, but reset for consistency

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
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
            return;
        }

        LogMessage($"Found {isoFilesToTest.Count} .iso files for testing.");
        UpdateSummaryStatsUi(isoFilesToTest.Count);
        UpdateProgressUi(0, isoFilesToTest.Count);
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
        LogMessage($"Starting test... Total .iso files to test: {isoFilesToTest.Count}.");

        var testFileIndex = 1; // Counter for simple test filenames

        foreach (var isoFilePath in isoFilesToTest)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var isoFileName = Path.GetFileName(isoFilePath);

            UpdateStatus($"Testing: {isoFileName}");
            LogMessage($"Testing ISO: {isoFileName}...");

            // Drive for testing (temp) vs. moving successful (output)
            SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath())); // Test extraction always uses temp path

            var testStatus = await TestSingleIsoAsync(extractXisoPath, isoFilePath, testFileIndex);
            testFileIndex++;
            actualIsosProcessedForProgress++;

            if (testStatus == IsoTestResultStatus.Passed)
            {
                _uiSuccessCount++;
                LogMessage($"  SUCCESS: '{isoFileName}' passed test.");

                if (moveSuccessful && !string.IsNullOrEmpty(successFolder))
                {
                    SetCurrentOperationDrive(GetDriveLetter(successFolder)); // Switch drive for move operation
                    await MoveTestedFileAsync(isoFilePath, successFolder, "successfully tested", _cts.Token);
                }
            }
            else // IsoTestResultStatus.Failed
            {
                _uiFailedCount++;
                failedIsoOriginalPaths.Add(isoFilePath); // Add original path before potential move
                LogMessage($"  FAILURE: '{isoFileName}' failed test.");

                if (moveFailed && !string.IsNullOrEmpty(failedFolder))
                {
                    SetCurrentOperationDrive(GetDriveLetter(failedFolder)); // Switch drive for move operation
                    await MoveTestedFileAsync(isoFilePath, failedFolder, "failed test", _cts.Token);
                }
            }

            UpdateSummaryStatsUi();
            UpdateProgressUi(actualIsosProcessedForProgress, isoFilesToTest.Count);
        }

        if (!_cts.Token.IsCancellationRequested && actualIsosProcessedForProgress >= isoFilesToTest.Count)
        {
            UpdateProgressUi(isoFilesToTest.Count, isoFilesToTest.Count);
        }

        if (failedIsoOriginalPaths.Count > 0 && _uiFailedCount > 0)
        {
            LogMessage("\nList of ISOs that failed the test (original names):");
            foreach (var originalPath in failedIsoOriginalPaths)
            {
                LogMessage($"- {Path.GetFileName(originalPath)}");
            }
        }

        if (_uiFailedCount > 0)
        {
            LogMessage("Failed ISOs may be corrupted or not valid Xbox ISO images. Check individual logs for details from extract-xiso.");
        }
    }

    private async Task MoveTestedFileAsync(string sourceFile, string destinationFolder, string moveReason, CancellationToken token)
    {
        var fileName = Path.GetFileName(sourceFile);
        var destinationFile = Path.Combine(destinationFolder, fileName);

        try
        {
            token.ThrowIfCancellationRequested();

            if (!await Task.Run(() => Directory.Exists(destinationFolder), token))
            {
                await Task.Run(() => Directory.CreateDirectory(destinationFolder), token);
            }

            token.ThrowIfCancellationRequested();

            if (await Task.Run(() => File.Exists(destinationFile), token))
            {
                LogMessage($"  Cannot move {fileName}: Destination file already exists at {destinationFile}. Skipping move.");
                return;
            }

            if (!await Task.Run(() => File.Exists(sourceFile), token))
            {
                LogMessage($"  Cannot move {fileName}: Source file no longer exists. It may have already been moved.");
                return;
            }

            token.ThrowIfCancellationRequested();

            await Task.Run(() => File.Move(sourceFile, destinationFile), token);
            LogMessage($"  Moved {fileName} ({moveReason}) to {destinationFolder}");
        }
        catch (OperationCanceledException)
        {
            LogMessage($"  Move operation for {fileName} cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"  Error moving {fileName} to {destinationFolder}: {ex.Message}");
            await ReportBugAsync($"Error moving tested file {fileName}", ex);
        }
    }

    private async Task<IsoTestResultStatus> TestSingleIsoAsync(string extractXisoPath, string isoFilePath, int fileIndex)
    {
        var isoFileName = Path.GetFileName(isoFilePath);
        var tempExtractionDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_TestExtract", Guid.NewGuid().ToString());
        string? simpleFilePath;

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

            // Always rename to simple filename for testing
            var simpleFilename = GenerateSimpleFilename(fileIndex);
            simpleFilePath = Path.Combine(tempExtractionDir, simpleFilename);

            LogMessage($"  Copying '{isoFileName}' to simple filename '{simpleFilename}' for testing");
            await Task.Run(() => File.Copy(isoFilePath, simpleFilePath, true), _cts.Token);

            var extractionSuccess = await RunIsoExtractionToTempAsync(extractXisoPath, simpleFilePath, tempExtractionDir);

            return extractionSuccess ? IsoTestResultStatus.Passed : IsoTestResultStatus.Failed;
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
            // Clean up temp dir which contains the simple file copy and any extracted contents
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

    private async Task<bool> RunIsoExtractionToTempAsync(string extractXisoPath, string inputFile, string tempExtractionDir)
    {
        var isoFileName = Path.GetFileName(inputFile);
        LogMessage($"    Detailed Extraction Attempt for: {isoFileName}");

        var processOutputCollector = new StringBuilder();
        Process? processRef;
        CancellationTokenRegistration cancellationRegistration = default;

        try
        {
            Directory.CreateDirectory(tempExtractionDir);

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
                    errLine.Contains("open error: -d No such file or directory") ||
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

    private async Task<FileProcessingStatus> ConvertFileAsync(string extractXisoPath, string inputFile, string outputFolder, bool deleteOriginalIsoFile, int fileIndex)
    {
        var originalFileName = Path.GetFileName(inputFile);
        var logPrefix = $"File '{originalFileName}':";
        string? simpleFilePath = null;

        try
        {
            // Generate simple filename
            var simpleFilename = GenerateSimpleFilename(fileIndex);
            var workingDirectory = Path.GetDirectoryName(inputFile) ?? Path.GetTempPath();
            simpleFilePath = Path.Combine(workingDirectory, simpleFilename);

            LogMessage($"{logPrefix} Renaming to simple filename '{simpleFilename}' for conversion");

            // Rename to simple filename
            await Task.Run(() => File.Move(inputFile, simpleFilePath, true), _cts.Token);

            var toolResult = await RunConversionToolAsync(extractXisoPath, simpleFilePath, originalFileName);

            if (toolResult == ConversionToolResultStatus.Failed)
            {
                LogMessage($"{logPrefix} extract-xiso tool reported failure.");

                // Restore original filename if conversion failed
                if (!await Task.Run(() => File.Exists(simpleFilePath), _cts.Token)) return FileProcessingStatus.Failed;

                await Task.Run(() => File.Move(simpleFilePath, inputFile, true), _cts.Token);
                LogMessage($"{logPrefix} Restored original filename after conversion failure");

                return FileProcessingStatus.Failed;
            }

            await Task.Run(() => Directory.CreateDirectory(outputFolder), _cts.Token);
            var destinationPath = Path.Combine(outputFolder, originalFileName); // Use original filename for destination
            var isTemporaryFileFromArchive = inputFile.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

            if (toolResult == ConversionToolResultStatus.Skipped)
            {
                LogMessage($"{logPrefix} Already optimized. Copying to output with original filename.");
                await Task.Run(() => Directory.CreateDirectory(outputFolder), _cts.Token);

                // Copy the file to the destination
                await Task.Run(() => File.Copy(simpleFilePath, destinationPath, true), _cts.Token);

                // Now, handle the original file (which is currently at simpleFilePath)
                if (deleteOriginalIsoFile && !isTemporaryFileFromArchive)
                {
                    LogMessage($"{logPrefix} Deleting original file as requested.");
                    await Task.Run(() => File.Delete(simpleFilePath), _cts.Token);
                }
                else
                {
                    // If we're not deleting, we must restore the original file by moving the simple-named file back.
                    LogMessage($"{logPrefix} Restoring original filename.");
                    await Task.Run(() => File.Move(simpleFilePath, inputFile, true), _cts.Token);
                }

                return FileProcessingStatus.Skipped;
            }

            // For successful conversion, the simple filename file has been converted in place
            var convertedFilePath = simpleFilePath;
            var originalBackupPath = simpleFilePath + ".old";

            LogMessage($"{logPrefix} Moving converted file to output with original filename: {destinationPath}");
            await Task.Run(() => File.Move(convertedFilePath, destinationPath, true), _cts.Token);

            // Handle backup file cleanup
            if (await Task.Run(() => File.Exists(originalBackupPath), _cts.Token))
            {
                if (deleteOriginalIsoFile && !isTemporaryFileFromArchive)
                {
                    LogMessage($"{logPrefix} Deleting backup of simple filename file (original will be deleted as requested)");
                    await Task.Run(() => File.Delete(originalBackupPath), _cts.Token);
                }
                else if (!isTemporaryFileFromArchive)
                {
                    LogMessage($"{logPrefix} Restoring original file from backup with original filename");
                    await Task.Run(() => File.Move(originalBackupPath, inputFile, true), _cts.Token);
                }
                else
                {
                    // Clean up backup of temporary file
                    LogMessage($"{logPrefix} Cleaning up backup of temporary simple filename file");
                    await Task.Run(() => File.Delete(originalBackupPath), _cts.Token);
                }
            }
            else if (deleteOriginalIsoFile && !isTemporaryFileFromArchive)
            {
                LogMessage($"{logPrefix} Original file was successfully converted and moved to destination.");
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
            _ = ReportBugAsync($"Error processing file: {originalFileName}", ex);
            return FileProcessingStatus.Failed;
        }
        finally
        {
            // Clean up any remaining simple filename file
            if (!string.IsNullOrEmpty(simpleFilePath))
            {
                try
                {
                    if (await Task.Run(() => File.Exists(simpleFilePath), CancellationToken.None))
                    {
                        await Task.Run(() => File.Delete(simpleFilePath), CancellationToken.None);
                        LogMessage($"{logPrefix} Cleaned up remaining simple filename file");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"{logPrefix} Error cleaning up simple filename file: {ex.Message}");
                }
            }
        }
    }

    private async Task<ConversionToolResultStatus> RunConversionToolAsync(string extractXisoPath, string inputFile, string originalFileName)
    {
        var simpleFileName = Path.GetFileName(inputFile);
        LogMessage($"Running extract-xiso -r on simple filename: {simpleFileName} (original: {originalFileName})");

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
                LogMessage($"Output from extract-xiso -r for {originalFileName}:\n{collectedToolOutputForLog}");
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
                    LogMessage($"extract-xiso -r for {originalFileName} exited with 1 but no 'skipped' message. Treating as failure.");
                    _ = ReportBugAsync($"extract-xiso -r for {originalFileName} exited 1 without skip message. Output: {string.Join(Environment.NewLine, localProcessOutputLines)}");
                    return ConversionToolResultStatus.Failed;
                default:
                    _ = ReportBugAsync($"extract-xiso -r failed for {originalFileName} with exit code {process.ExitCode}. Output: {string.Join(Environment.NewLine, localProcessOutputLines)}");
                    return ConversionToolResultStatus.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage($"extract-xiso -r operation for {originalFileName} was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error running extract-xiso -r for {originalFileName}: {ex.Message}");
            _ = ReportBugAsync($"Exception during extract-xiso -r for {originalFileName}", ex);
            return ConversionToolResultStatus.Failed;
        }
        finally
        {
            await cancellationRegistration.DisposeAsync();
        }
    }

    private async Task<bool> ExtractArchiveAsync(string archivePath, string extractionPath)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        LogMessage($"Extracting: {archiveFileName} using SevenZipExtractor to {extractionPath}");

        try
        {
            await Task.Run(() =>
            {
                using var extractor = new SevenZipExtractor(archivePath);
                extractor.ExtractArchive(extractionPath); // Extract all files
            }, _cts.Token);

            LogMessage($"Successfully extracted: {archiveFileName}");
            return true;
        }
        catch (OperationCanceledException)
        {
            LogMessage($"Extraction of {archiveFileName} was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            LogMessage($"Error extracting {archiveFileName}: {ex.Message}");
            _ = ReportBugAsync($"Error extracting {archiveFileName}", ex);
            return false;
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

    private void LogOperationSummary(string operationType)
    {
        LogMessage("");
        LogMessage($"--- Batch {operationType.ToLowerInvariant()} completed. ---");
        LogMessage($"Total files processed: {_uiTotalFiles}");
        LogMessage($"Successfully {GetPastTense(operationType)}: {_uiSuccessCount} files");
        LogMessage($"Skipped: {_uiSkippedCount} files");
        if (_uiFailedCount > 0) LogMessage($"Failed to {operationType.ToLowerInvariant()}: {_uiFailedCount} files");

        Application.Current.Dispatcher.InvokeAsync(() =>
            ShowMessageBox($"Batch {operationType.ToLowerInvariant()} completed.\n\n" +
                           $"Total files processed: {_uiTotalFiles}\n" +
                           $"Successfully {GetPastTense(operationType)}: {_uiSuccessCount} files\n" +
                           $"Skipped: {_uiSkippedCount} files\n" +
                           $"Failed: {_uiFailedCount} files",
                $"{operationType} Complete", MessageBoxButton.OK,
                _uiFailedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information));
    }

    private string GetPastTense(string verb)
    {
        return verb.ToLowerInvariant() switch
        {
            "conversion" => "converted",
            "test" => "tested",
            _ => verb.ToLowerInvariant() + "ed"
        };
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
            /* ignored */
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

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var (isNewVersionAvailable, latestVersion, downloadUrl) = await _updateChecker.CheckForUpdateAsync();

            if (isNewVersionAvailable && !string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(latestVersion))
            {
                var result = MessageBox.Show(
                    this,
                    $"A new version ({latestVersion}) is available. Would you like to go to the download page?",
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    OpenUrl(downloadUrl);
                }
            }
        }
        catch (Exception ex)
        {
            // Log and report the error, but don't bother the user.
            LogMessage($"Error checking for updates: {ex.Message}");
            _ = ReportBugAsync("Error during update check", ex);
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            process?.Dispose();
        }
        catch (Exception ex)
        {
            LogMessage($"Error opening URL: {url}. Exception: {ex.Message}");
            _ = ReportBugAsync($"Error opening URL: {url}", ex);
            ShowError($"Unable to open link: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _processingTimer.Tick -= ProcessingTimer_Tick;
        _processingTimer.Stop();
        StopPerformanceCounter();
        _cts?.Cancel();
        _cts?.Dispose();
        _bugReportService?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            LogMessage($"Error opening About window: {ex.Message}");
            _ = ReportBugAsync("Error opening About window", ex);
        }
    }
}
