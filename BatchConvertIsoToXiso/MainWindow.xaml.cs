using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Threading;
using System.Windows.Controls;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BatchConvertIsoToXiso;

public partial class MainWindow : IDisposable
{
    private CancellationTokenSource _cts;
    private readonly IUpdateChecker _updateChecker;
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly IFileExtractor _fileExtractor;
    private readonly IFileMover _fileMover;
    private readonly IUrlOpener _urlOpener;
    private readonly IServiceProvider _serviceProvider;

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

    // Minimum required free space on the temporary drive for operations
    private const long MinimumRequiredConversionTempSpaceBytes = 10L * 1024 * 1024 * 1024; // 10 GB
    private const long MinimumRequiredTestTempSpaceBytes = 20L * 1024 * 1024 * 1024; // 20 GB (for ISO copy + full extraction)

    public MainWindow(IUpdateChecker updateChecker, ILogger logger, IBugReportService bugReportService, IMessageBoxService messageBoxService, IFileExtractor fileExtractor, IFileMover fileMover, IUrlOpener urlOpener, IServiceProvider serviceProvider)
    {
        InitializeComponent();

        _updateChecker = updateChecker;
        _logger = logger;
        _bugReportService = bugReportService;
        _messageBoxService = messageBoxService;
        _fileExtractor = fileExtractor;
        _fileMover = fileMover;
        _urlOpener = urlOpener;
        _serviceProvider = serviceProvider;

        _logger.Initialize(LogViewer);

        _cts = new CancellationTokenSource();

        _processingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _processingTimer.Tick += ProcessingTimer_Tick;
        ResetSummaryStats();

        DisplayInitialInstructions();

        Loaded += async (s, e) => await CheckForUpdatesAsync();
    }

    private void DisplayInitialInstructions()
    {
        _logger.LogMessage("Welcome to the Batch Convert ISO to XISO & Test Tool.");
        _logger.LogMessage("");
        _logger.LogMessage("This application provides two main functions, available in the tabs above:");
        _logger.LogMessage("1. Convert to XISO: Converts standard Xbox ISO files to the optimized XISO format. It can also process ISOs found within .zip, .7z, and .rar archives.");
        _logger.LogMessage("2. Test ISO Integrity: Verifies the integrity of your .iso files by attempting a full extraction to a temporary location.");
        _logger.LogMessage("");
        _logger.LogMessage("General Steps:");
        _logger.LogMessage("- Select the appropriate tab for the operation you want to perform.");
        _logger.LogMessage("- Use the 'Browse' buttons to select your source and destination folders.");
        _logger.LogMessage("- Configure the options for your chosen operation.");
        _logger.LogMessage("- Click the 'Start' button to begin.");
        _logger.LogMessage("");

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");
        if (File.Exists(extractXisoPath))
        {
            _logger.LogMessage("INFO: extract-xiso.exe found in the application directory.");
        }
        else
        {
            _logger.LogMessage("WARNING: extract-xiso.exe not found. ISO conversion and testing will fail.");
            _ = ReportBugAsync("extract-xiso.exe not found.");
        }

        _logger.LogMessage("INFO: Archive extraction uses the SevenZipExtractor library.");
        _logger.LogMessage("--- Ready ---");
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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
            _logger.LogMessage($"Could not determine drive letter for path: {path}. It might be a network path or invalid.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error getting drive letter for path {path}: {ex.Message}");
            return null;
        }
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
            _logger.LogMessage("Cannot monitor write speed: Drive letter is invalid or not determined (e.g., network path).");
            Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A");
            return;
        }

        var perfCounterInstanceName = driveLetter.EndsWith(':') ? driveLetter : driveLetter + ":";

        try
        {
            // First, check if the category exists. If not, we can't proceed.
            if (!PerformanceCounterCategory.Exists("LogicalDisk"))
            {
                _logger.LogMessage($"Performance counter category 'LogicalDisk' does not exist. Cannot monitor write speed for drive {perfCounterInstanceName}.");
                Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A (Category Missing)");
                return;
            }

            // Now, check if the specific instance exists for the given drive letter.
            if (!PerformanceCounterCategory.InstanceExists(perfCounterInstanceName, "LogicalDisk"))
            {
                _logger.LogMessage($"Performance counter instance '{perfCounterInstanceName}' not found for 'LogicalDisk'. Cannot monitor write speed for this drive.");
                Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A (Instance Missing)");
                return;
            }

            // If both category and instance exist, proceed with creating the counter.
            _diskWriteSpeedCounter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", perfCounterInstanceName, true);
            _diskWriteSpeedCounter.NextValue(); // Initial call to prime the counter
            _activeMonitoringDriveLetter = driveLetter;
            _logger.LogMessage($"Monitoring write speed for drive: {perfCounterInstanceName}");
            Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "Calculating...");
        }
        catch (InvalidOperationException ex)
        {
            // This catch block should now primarily handle issues during counter creation/access after existence checks.
            _logger.LogMessage($"Error initializing performance counter for drive {perfCounterInstanceName}: {ex.Message}. Write speed monitoring disabled.");
            _ = ReportBugAsync($"PerfCounter Init InvalidOpExc for {perfCounterInstanceName}", ex);
            _diskWriteSpeedCounter?.Dispose();
            _diskWriteSpeedCounter = null;
            _activeMonitoringDriveLetter = null;
            Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A (Error)");
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Unexpected error initializing performance counter for drive {perfCounterInstanceName}: {ex.Message}. Write speed monitoring disabled.");
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

    private void BrowseConversionInputButton_Click(object sender, RoutedEventArgs e)
    {
        var inputFolder = SelectFolder("Select the folder containing ISO or archive files");
        if (string.IsNullOrEmpty(inputFolder)) return;

        ConversionInputFolderTextBox.Text = inputFolder;
        _logger.LogMessage($"Conversion input folder selected: {inputFolder}");
    }

    private void BrowseConversionOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var outputFolder = SelectFolder("Select the output folder for converted XISO files");
        if (string.IsNullOrEmpty(outputFolder)) return;

        ConversionOutputFolderTextBox.Text = outputFolder;
        _logger.LogMessage($"Conversion output folder selected: {outputFolder}");
    }

    private void BrowseTestInputButton_Click(object sender, RoutedEventArgs e)
    {
        var inputFolder = SelectFolder("Select the folder containing ISO files to test");
        if (string.IsNullOrEmpty(inputFolder)) return;

        TestInputFolderTextBox.Text = inputFolder;
        _logger.LogMessage($"Test input folder selected: {inputFolder}");
    }

    private async void StartConversionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() => LogViewer.Clear());

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");

            if (!await Task.Run(() => File.Exists(extractXisoPath), _cts.Token))
            {
                _logger.LogMessage("Error: extract-xiso.exe not found.");
                _messageBoxService.ShowError("extract-xiso.exe is missing. Please ensure it's in the application folder.");
                _ = ReportBugAsync("extract-xiso.exe not found at start of conversion.", new FileNotFoundException("extract-xiso.exe missing", extractXisoPath));
                StopPerformanceCounter();
                return;
            }

            var inputFolder = ConversionInputFolderTextBox.Text;
            var outputFolder = ConversionOutputFolderTextBox.Text;
            var deleteFiles = DeleteOriginalsCheckBox.IsChecked ?? false;
            var skipSystemUpdate = SkipSystemUpdateCheckBox.IsChecked ?? false; // Get state of new checkbox

            if (string.IsNullOrEmpty(inputFolder) || string.IsNullOrEmpty(outputFolder))
            {
                _messageBoxService.ShowError("Please select both input and output folders for conversion.");
                StopPerformanceCounter();
                return;
            }

            if (inputFolder.Equals(outputFolder, StringComparison.OrdinalIgnoreCase))
            {
                _messageBoxService.ShowError("Input and output folders must be different for conversion.");
                StopPerformanceCounter();
                return;
            }

            // Check for sufficient temporary disk space
            var tempPath = Path.GetTempPath();
            var tempDriveLetter = GetDriveLetter(tempPath);
            if (string.IsNullOrEmpty(tempDriveLetter))
            {
                _messageBoxService.ShowError($"Could not determine drive for temporary path '{tempPath}'. Cannot proceed with conversion.");
                StopPerformanceCounter();
                _ = ReportBugAsync($"Could not determine drive for temporary path '{tempPath}' at start of conversion.");
                return;
            }

            var tempDriveInfo = new DriveInfo(tempDriveLetter);
            if (!tempDriveInfo.IsReady)
            {
                _messageBoxService.ShowError($"Temporary drive '{tempDriveLetter}' is not ready. Cannot proceed with conversion.");
                StopPerformanceCounter();
                _ = ReportBugAsync($"Temporary drive '{tempDriveLetter}' not ready at start of conversion.");
                return;
            }

            if (tempDriveInfo.AvailableFreeSpace < MinimumRequiredConversionTempSpaceBytes)
            {
                _messageBoxService.ShowError($"Insufficient free space on temporary drive ({tempDriveLetter}). " +
                                             $"Required: {Formatter.FormatBytes(MinimumRequiredConversionTempSpaceBytes)}, Available: {Formatter.FormatBytes(tempDriveInfo.AvailableFreeSpace)}. " +
                                             "Please free up space or choose a different temporary drive if possible (via system settings).");
                StopPerformanceCounter();
                _ = ReportBugAsync($"Insufficient temp space on drive {tempDriveLetter} for conversion. Available: {tempDriveInfo.AvailableFreeSpace}");
                return;
            }

            _logger.LogMessage($"INFO: Temporary drive '{tempDriveLetter}' has {Formatter.FormatBytes(tempDriveInfo.AvailableFreeSpace)} free space (required: {Formatter.FormatBytes(MinimumRequiredConversionTempSpaceBytes)}).");

            ResetSummaryStats();

            // Start with output drive but allow dynamic switching
            var outputDrive = GetDriveLetter(outputFolder);
            _currentOperationDrive = outputDrive;
            InitializePerformanceCounter(outputDrive);

            _operationStartTime = DateTime.Now;
            _processingTimer.Start();

            SetControlsState(false);
            UpdateStatus("Starting batch conversion...");
            _logger.LogMessage("--- Starting batch conversion process... ---");
            _logger.LogMessage($"Input folder: {inputFolder}");
            _logger.LogMessage($"Output folder: {outputFolder}");
            _logger.LogMessage($"Delete original files: {deleteFiles}");
            _logger.LogMessage($"Skip $SystemUpdate folder: {skipSystemUpdate}"); // Log new option

            try
            {
                await PerformBatchConversionAsync(extractXisoPath, inputFolder, outputFolder, deleteFiles, skipSystemUpdate);
            }
            catch (OperationCanceledException)
            {
                _logger.LogMessage("Operation was canceled by user.");
                UpdateStatus("Operation cancelled. Ready.");
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Critical Error: {ex.Message}");
                UpdateStatus("An error occurred. Ready.");
                _ = ReportBugAsync("Critical error during batch conversion process", ex);
            }
            finally
            {
                _processingTimer.Stop();
                StopPerformanceCounter();
                _currentOperationDrive = null;
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
            _logger.LogMessage($"Error during batch conversion process: {ex.Message}");
            StopPerformanceCounter();
        }
    }

    private async void StartTestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() => LogViewer.Clear());

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");

            if (!await Task.Run(() => File.Exists(extractXisoPath), _cts.Token))
            {
                _logger.LogMessage("Error: extract-xiso.exe not found.");
                _messageBoxService.ShowError("extract-xiso.exe is missing. Please ensure it's in the application folder.");
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
                _messageBoxService.ShowError("Please select the input folder for testing.");
                StopPerformanceCounter();
                return;
            }

            if (moveSuccessful && moveFailed && !string.IsNullOrEmpty(successFolder) && successFolder.Equals(failedFolder, StringComparison.OrdinalIgnoreCase))
            {
                _messageBoxService.ShowError("Success Folder and Failed Folder cannot be the same.");
                StopPerformanceCounter();
                return;
            }

            if ((moveSuccessful && !string.IsNullOrEmpty(successFolder) && successFolder.Equals(inputFolder, StringComparison.OrdinalIgnoreCase)) ||
                (moveFailed && !string.IsNullOrEmpty(failedFolder) && failedFolder.Equals(inputFolder, StringComparison.OrdinalIgnoreCase)))
            {
                _messageBoxService.ShowError("Success/Failed folder cannot be the same as the Input folder.");
                StopPerformanceCounter();
                return;
            }

            // Check for sufficient temporary disk space
            var tempPath = Path.GetTempPath();
            var tempDriveLetter = GetDriveLetter(tempPath);
            if (string.IsNullOrEmpty(tempDriveLetter))
            {
                _messageBoxService.ShowError($"Could not determine drive for temporary path '{tempPath}'. Cannot proceed with ISO test.");
                StopPerformanceCounter();
                _ = ReportBugAsync($"Could not determine drive for temporary path '{tempPath}' at start of ISO test.");
                return;
            }

            var tempDriveInfo = new DriveInfo(tempDriveLetter);
            if (!tempDriveInfo.IsReady)
            {
                _messageBoxService.ShowError($"Temporary drive '{tempDriveLetter}' is not ready. Cannot proceed with ISO test.");
                StopPerformanceCounter();
                _ = ReportBugAsync($"Temporary drive '{tempDriveLetter}' not ready at start of ISO test.");
                return;
            }

            if (tempDriveInfo.AvailableFreeSpace < MinimumRequiredTestTempSpaceBytes)
            {
                _messageBoxService.ShowError($"Insufficient free space on temporary drive ({tempDriveLetter}). " +
                                             $"Required: {Formatter.FormatBytes(MinimumRequiredTestTempSpaceBytes)}, Available: {Formatter.FormatBytes(tempDriveInfo.AvailableFreeSpace)}. " +
                                             "Please free up space or choose a different temporary drive if possible (via system settings).");
                StopPerformanceCounter();
                _ = ReportBugAsync($"Insufficient temp space on drive {tempDriveLetter} for ISO test. Available: {tempDriveInfo.AvailableFreeSpace}");
                return;
            }

            _logger.LogMessage($"INFO: Temporary drive '{tempDriveLetter}' has {Formatter.FormatBytes(tempDriveInfo.AvailableFreeSpace)} free space (required: {Formatter.FormatBytes(MinimumRequiredTestTempSpaceBytes)}).");

            ResetSummaryStats();

            var initialDriveForMonitoring = moveSuccessful ? GetDriveLetter(successFolder) : GetDriveLetter(Path.GetTempPath());
            _currentOperationDrive = initialDriveForMonitoring; // Initial drive
            InitializePerformanceCounter(initialDriveForMonitoring);

            _operationStartTime = DateTime.Now;
            _processingTimer.Start();

            SetControlsState(false);
            UpdateStatus("Starting batch ISO test...");
            _logger.LogMessage("--- Starting batch ISO test process... ---");
            _logger.LogMessage($"Input folder: {inputFolder}");
            if (moveSuccessful) _logger.LogMessage($"Moving successful files to: {successFolder}");
            if (moveFailed) _logger.LogMessage($"Moving failed files to: {failedFolder}");

            try
            {
                await PerformBatchIsoTestAsync(extractXisoPath, inputFolder, moveSuccessful, successFolder, moveFailed, failedFolder);
            }
            catch (OperationCanceledException)
            {
                _logger.LogMessage("Operation was canceled by user.");
                UpdateStatus("Operation cancelled. Ready.");
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Critical Error during ISO test: {ex.Message}");
                UpdateStatus("An error occurred. Ready.");
                _ = ReportBugAsync("Critical error during batch ISO test process", ex);
            }
            finally
            {
                _processingTimer.Stop();
                StopPerformanceCounter();
                _currentOperationDrive = null;
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
            _logger.LogMessage($"Error during batch ISO test process: {ex.Message}");
            StopPerformanceCounter();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        _logger.LogMessage("Cancellation requested. Finishing current file/archive...");
    }

    private void SetControlsState(bool enabled)
    {
        // Conversion Tab
        ConversionInputFolderTextBox.IsEnabled = enabled;
        BrowseConversionInputButton.IsEnabled = enabled;
        ConversionOutputFolderTextBox.IsEnabled = enabled;
        BrowseConversionOutputButton.IsEnabled = enabled;
        DeleteOriginalsCheckBox.IsEnabled = enabled;
        SkipSystemUpdateCheckBox.IsEnabled = enabled;
        StartConversionButton.IsEnabled = enabled;

        // Test Tab
        TestInputFolderTextBox.IsEnabled = enabled;
        BrowseTestInputButton.IsEnabled = enabled;
        MoveSuccessFilesCheckBox.IsEnabled = enabled;
        MoveFailedFilesCheckBox.IsEnabled = enabled;
        StartTestButton.IsEnabled = enabled;

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
                    WriteSpeedValue.Text = Formatter.FormatBytesPerSecond(writeSpeedBytes);
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogMessage($"Error reading write speed for drive {_activeMonitoringDriveLetter}: {ex.Message}. Stopping monitoring.");
            _ = ReportBugAsync($"PerfCounter Read InvalidOpExc for {_activeMonitoringDriveLetter}", ex);
            StopPerformanceCounter();
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Unexpected error reading write speed for drive {_activeMonitoringDriveLetter}: {ex.Message}. Stopping monitoring.");
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

    private static string GenerateSimpleFilename(int fileIndex)
    {
        return $"iso_{fileIndex:D6}.iso";
    }

    private async Task PerformBatchConversionAsync(string extractXisoPath, string inputFolder, string outputFolder, bool deleteOriginals, bool skipSystemUpdate) // Add skipSystemUpdate parameter
    {
        var tempFoldersToCleanUpAtEnd = new List<string>();
        var archivesFailedToExtractOrProcess = 0;
        var actualIsosProcessedForProgress = 0;
        var failedConversionFilePaths = new List<string>();

        _uiSuccessCount = 0;
        _uiFailedCount = 0;
        _uiSkippedCount = 0;

        _logger.LogMessage("Scanning input folder for items to convert...");
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
            _logger.LogMessage($"Error scanning input folder: {ex.Message}");
            _ = ReportBugAsync("Error scanning input folder", ex);
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
            return;
        }

        if (initialEntriesToProcess.Count == 0)
        {
            _logger.LogMessage("No ISO files or supported archives found in the input folder for conversion.");
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
            return;
        }

        _logger.LogMessage($"Found {initialEntriesToProcess.Count} top-level items (ISOs or archives) for conversion.");
        var currentExpectedTotalIsos = initialEntriesToProcess.Count;
        UpdateSummaryStatsUi(currentExpectedTotalIsos);
        UpdateProgressUi(0, currentExpectedTotalIsos);
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
        _logger.LogMessage($"Starting conversion... Total items to process initially: {currentExpectedTotalIsos}. This may increase if archives contain multiple ISOs.");

        var globalFileIndex = 1; // Global counter for simple filenames

        foreach (var currentEntryPath in initialEntriesToProcess)
        {
            _cts.Token.ThrowIfCancellationRequested();

            var entryFileName = Path.GetFileName(currentEntryPath);
            UpdateStatus($"Processing: {entryFileName}");
            var entryExtension = Path.GetExtension(currentEntryPath).ToLowerInvariant();

            switch (entryExtension)
            {
                case ".iso":
                {
                    _logger.LogMessage($"Processing standalone ISO: {entryFileName}...");
                    var status = await ConvertFileAsync(extractXisoPath, currentEntryPath, outputFolder, deleteOriginals, globalFileIndex, skipSystemUpdate); // Pass new option
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
                    break;
                }
                case ".zip" or ".7z" or ".rar":
                {
                    _logger.LogMessage($"Processing archive: {entryFileName}...");
                    var statusesOfIsosInThisArchive = new List<FileProcessingStatus>();
                    string? currentArchiveTempExtractionDir = null;
                    var archiveExtractedSuccessfully = false;

                    try
                    {
                        currentArchiveTempExtractionDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_Extract", Guid.NewGuid().ToString());
                        await Task.Run(() => Directory.CreateDirectory(currentArchiveTempExtractionDir), _cts.Token);
                        tempFoldersToCleanUpAtEnd.Add(currentArchiveTempExtractionDir);
                        SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath()));
                        archiveExtractedSuccessfully = await _fileExtractor.ExtractArchiveAsync(currentEntryPath, currentArchiveTempExtractionDir, _cts);
                        if (archiveExtractedSuccessfully)
                        {
                            var extractedIsoFiles = await Task.Run(() => Directory.GetFiles(currentArchiveTempExtractionDir, "*.iso", SearchOption.AllDirectories), _cts.Token);
                            switch (extractedIsoFiles.Length)
                            {
                                case > 0:
                                {
                                    var newIsosFound = extractedIsoFiles.Length;
                                    currentExpectedTotalIsos += (newIsosFound - 1);
                                    UpdateSummaryStatsUi(currentExpectedTotalIsos);
                                    UpdateProgressUi(actualIsosProcessedForProgress, currentExpectedTotalIsos);
                                    _logger.LogMessage($"Found {newIsosFound} ISO(s) in {entryFileName}. Total expected ISOs now: {currentExpectedTotalIsos}. Processing them now...");
                                    break;
                                }
                                case 0:
                                    _logger.LogMessage($"No ISO files found in archive: {entryFileName}.");
                                    actualIsosProcessedForProgress++;
                                    UpdateProgressUi(actualIsosProcessedForProgress, currentExpectedTotalIsos);
                                    break;
                            }

                            foreach (var extractedIsoPath in extractedIsoFiles)
                            {
                                _cts.Token.ThrowIfCancellationRequested();
                                var extractedIsoName = Path.GetFileName(extractedIsoPath);
                                _logger.LogMessage($"  Converting ISO from archive: {extractedIsoName}...");
                                var status = await ConvertFileAsync(extractXisoPath, extractedIsoPath, outputFolder, false, globalFileIndex, skipSystemUpdate); // Pass new option
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
                            _logger.LogMessage($"Failed to extract archive: {entryFileName}. It will be skipped.");
                            archivesFailedToExtractOrProcess++;
                            statusesOfIsosInThisArchive.Add(FileProcessingStatus.Failed);
                            failedConversionFilePaths.Add(currentEntryPath);
                            actualIsosProcessedForProgress++;
                            _uiFailedCount++;
                            UpdateSummaryStatsUi();
                            UpdateProgressUi(actualIsosProcessedForProgress, currentExpectedTotalIsos);
                        }

                        switch (deleteOriginals)
                        {
                            case true when archiveExtractedSuccessfully:
                            {
                                var allIsosFromArchiveOk = statusesOfIsosInThisArchive.Count > 0 &&
                                                           statusesOfIsosInThisArchive.All(static s => s is FileProcessingStatus.Converted or FileProcessingStatus.Skipped);
                                if (allIsosFromArchiveOk)
                                {
                                    _logger.LogMessage($"All contents of archive {entryFileName} processed successfully. Deleting original archive.");
                                    await TryDeleteFileAsync(currentEntryPath);
                                }
                                else if (statusesOfIsosInThisArchive.Count > 0)
                                {
                                    _logger.LogMessage($"Not deleting archive {entryFileName} due to processing issues with its contents.");
                                }

                                break;
                            }
                            case true when !archiveExtractedSuccessfully:
                                _logger.LogMessage($"Not deleting archive {entryFileName} due to extraction failure.");
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogMessage($"Error processing archive {entryFileName}: {ex.Message}");
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
                                _logger.LogMessage($"Cleaned up temporary folder for {entryFileName}.");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogMessage($"Error cleaning temp folder {currentArchiveTempExtractionDir} for {entryFileName}: {ex.Message}. Will retry at end.");
                            }
                        }
                    }

                    break;
                }
            }
        }

        if (!_cts.Token.IsCancellationRequested && actualIsosProcessedForProgress >= currentExpectedTotalIsos)
        {
            UpdateProgressUi(currentExpectedTotalIsos, currentExpectedTotalIsos);
        }

        if (archivesFailedToExtractOrProcess > 0)
        {
            _logger.LogMessage($"Note: {archivesFailedToExtractOrProcess} archive(s) failed to extract or had processing errors.");
        }

        if (failedConversionFilePaths.Count > 0)
        {
            _logger.LogMessage("\nList of items that failed conversion or archive extraction:");
            foreach (var failedPath in failedConversionFilePaths)
            {
                _logger.LogMessage($"- {Path.GetFileName(failedPath)} (Full path: {failedPath})");
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
        _uiSkippedCount = 0;

        _logger.LogMessage("Scanning input folder for .iso files to test...");
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = true);

        List<string> isoFilesToTest;
        try
        {
            isoFilesToTest = await Task.Run(() => Directory.GetFiles(inputFolder, "*.iso", SearchOption.TopDirectoryOnly).ToList(), _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error scanning input folder for .iso files: {ex.Message}");
            _ = ReportBugAsync("Error scanning input folder for .iso files for testing", ex);
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
            return;
        }

        if (isoFilesToTest.Count == 0)
        {
            _logger.LogMessage("No .iso files found in the input folder for testing.");
            Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
            return;
        }

        _logger.LogMessage($"Found {isoFilesToTest.Count} .iso files for testing.");
        UpdateSummaryStatsUi(isoFilesToTest.Count);
        UpdateProgressUi(0, isoFilesToTest.Count);
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
        _logger.LogMessage($"Starting test... Total .iso files to test: {isoFilesToTest.Count}.");

        var testFileIndex = 1; // Counter for simple test filenames

        foreach (var isoFilePath in isoFilesToTest)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var isoFileName = Path.GetFileName(isoFilePath);

            UpdateStatus($"Testing: {isoFileName}");
            _logger.LogMessage($"Testing ISO: {isoFileName}...");

            // Drive for testing (temp) vs. moving successful (output)
            SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath())); // Test extraction always uses temp path

            var testStatus = await TestSingleIsoAsync(extractXisoPath, isoFilePath, testFileIndex);
            testFileIndex++;
            actualIsosProcessedForProgress++;

            if (testStatus == IsoTestResultStatus.Passed)
            {
                _uiSuccessCount++;
                _logger.LogMessage($"  SUCCESS: '{isoFileName}' passed test.");

                if (moveSuccessful && !string.IsNullOrEmpty(successFolder))
                {
                    SetCurrentOperationDrive(GetDriveLetter(successFolder)); // Switch drive for move operation
                    await _fileMover.MoveTestedFileAsync(isoFilePath, successFolder, "successfully tested", _cts.Token);
                }
            }
            else // IsoTestResultStatus.Failed
            {
                _uiFailedCount++;
                failedIsoOriginalPaths.Add(isoFilePath); // Add original path before potential move
                _logger.LogMessage($"  FAILURE: '{isoFileName}' failed test.");

                if (moveFailed && !string.IsNullOrEmpty(failedFolder))
                {
                    SetCurrentOperationDrive(GetDriveLetter(failedFolder)); // Switch drive for move operation
                    await _fileMover.MoveTestedFileAsync(isoFilePath, failedFolder, "failed test", _cts.Token);
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
            _logger.LogMessage("\nList of ISOs that failed the test (original names):");
            foreach (var originalPath in failedIsoOriginalPaths)
            {
                _logger.LogMessage($"- {Path.GetFileName(originalPath)}");
            }
        }

        if (_uiFailedCount > 0)
        {
            _logger.LogMessage("Failed ISOs may be corrupted or not valid Xbox ISO images. Check individual logs for details from extract-xiso.");
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
            _logger.LogMessage($"  Comprehensive Test for '{isoFileName}'");

            if (!await Task.Run(() => File.Exists(isoFilePath), _cts.Token))
            {
                _logger.LogMessage($"  ERROR: ISO file does not exist: {isoFilePath}");
                return IsoTestResultStatus.Failed;
            }

            try
            {
                var fileInfo = new FileInfo(isoFilePath);
                var length = await Task.Run(() => fileInfo.Length, _cts.Token);
                if (length == 0)
                {
                    _logger.LogMessage($"  ERROR: ISO file is empty: {isoFileName}");
                    return IsoTestResultStatus.Failed;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"  ERROR: Cannot open or check ISO file: {ex.Message}");
                _ = ReportBugAsync($"Error checking file {isoFileName} in TestSingleIsoAsync", ex);
                return IsoTestResultStatus.Failed;
            }

            // Always rename to simple filename for testing
            var simpleFilename = GenerateSimpleFilename(fileIndex);
            simpleFilePath = Path.Combine(tempExtractionDir, simpleFilename);

            _logger.LogMessage($"  Copying '{isoFileName}' to simple filename '{simpleFilename}' for testing");
            await Task.Run(() => File.Copy(isoFilePath, simpleFilePath, true), _cts.Token);

            var extractionSuccess = await RunIsoExtractionToTempAsync(extractXisoPath, simpleFilePath, tempExtractionDir);

            return extractionSuccess ? IsoTestResultStatus.Passed : IsoTestResultStatus.Failed;
        }
        catch (OperationCanceledException)
        {
            _logger.LogMessage($"  Test for '{isoFileName}' was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"  Unexpected error testing '{isoFileName}': {ex.Message}");
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
                _logger.LogMessage($"  Error cleaning temp folder for '{isoFileName}': {ex.Message}");
            }
        }
    }

    private async Task<bool> RunIsoExtractionToTempAsync(string extractXisoPath, string inputFile, string tempExtractionDir)
    {
        var isoFileName = Path.GetFileName(inputFile);
        _logger.LogMessage($"    Detailed Extraction Attempt for: {isoFileName}");

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
                    // Ignore
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
            _logger.LogMessage($"    Process Exit Code for '{isoFileName}': {process.ExitCode}");
            _logger.LogMessage($"    Full Output Log from extract-xiso -x for '{isoFileName}':\n{collectedOutput}");

            var isoNameWithoutExtension = Path.GetFileNameWithoutExtension(isoFileName);
            var expectedExtractionSubDir = Path.Combine(tempExtractionDir, isoNameWithoutExtension);

            var filesWereExtracted = false;
            if (await Task.Run(() => Directory.Exists(expectedExtractionSubDir), _cts.Token))
            {
                var extractedFiles = await Task.Run(() => Directory.GetFiles(expectedExtractionSubDir, "*", SearchOption.AllDirectories), _cts.Token);
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
            await cancellationRegistration.DisposeAsync();
        }
    }

    private async Task<FileProcessingStatus> ConvertFileAsync(string extractXisoPath, string inputFile, string outputFolder, bool deleteOriginalIsoFile, int fileIndex, bool skipSystemUpdate) // Add skipSystemUpdate parameter
    {
        var originalFileName = Path.GetFileName(inputFile);
        var logPrefix = $"File '{originalFileName}':";
        string? localTempIsoPath; // Path to the ISO in the local temp directory
        string? localTempWorkingDir = null; // Directory for local temp operations

        try
        {
            // 1. Create a local temporary directory for this file's processing
            localTempWorkingDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_Convert", Guid.NewGuid().ToString());
            await Task.Run(() => Directory.CreateDirectory(localTempWorkingDir), _cts.Token);
            _logger.LogMessage($"{logPrefix} Created local temporary working directory: {localTempWorkingDir}");

            // 2. Generate simple filename for the local copy
            var simpleFilename = GenerateSimpleFilename(fileIndex);
            localTempIsoPath = Path.Combine(localTempWorkingDir, simpleFilename);

            // 3. Copy the original file from source (potentially UNC) to local temp
            _logger.LogMessage($"{logPrefix} Copying from '{inputFile}' to local temp '{localTempIsoPath}'...");
            SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath())); // Monitor temp drive for copy write
            await Task.Run(() => File.Copy(inputFile, localTempIsoPath, true), _cts.Token);
            _logger.LogMessage($"{logPrefix} Successfully copied to local temp.");

            // 4. Run extract-xiso on the local temporary copy
            SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath())); // Still monitoring temp drive for in-place rewrite
            var toolResult = await RunConversionToolAsync(extractXisoPath, localTempIsoPath, originalFileName, skipSystemUpdate); // Pass new option

            var isTemporaryFileFromArchive = inputFile.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

            if (toolResult == ConversionToolResultStatus.Failed)
            {
                _logger.LogMessage($"{logPrefix} extract-xiso tool reported failure on local copy.");
                // No need to restore original filename as original is untouched.
                return FileProcessingStatus.Failed;
            }

            // Ensure output folder exists
            await Task.Run(() => Directory.CreateDirectory(outputFolder), _cts.Token);
            var destinationPath = Path.Combine(outputFolder, originalFileName); // Use original filename for destination

            if (toolResult == ConversionToolResultStatus.Skipped)
            {
                _logger.LogMessage($"{logPrefix} Already optimized. Moving local copy to output with original filename.");
                SetCurrentOperationDrive(GetDriveLetter(outputFolder)); // Monitor output drive for move write
                await Task.Run(() => File.Move(localTempIsoPath, destinationPath, true), _cts.Token);

                if (deleteOriginalIsoFile && !isTemporaryFileFromArchive)
                {
                    _logger.LogMessage($"{logPrefix} Deleting original file as requested.");
                    await TryDeleteFileAsync(inputFile);
                }
                else if (isTemporaryFileFromArchive)
                {
                    // Original file was already temporary, it will be cleaned up by archive processing logic
                    _logger.LogMessage($"{logPrefix} Original file was temporary (from archive), it will be cleaned up by archive logic.");
                }
                else
                {
                    _logger.LogMessage($"{logPrefix} Original file kept as requested.");
                }

                return FileProcessingStatus.Skipped;
            }

            // If toolResult is Success
            _logger.LogMessage($"{logPrefix} Moving converted local file to output with original filename: {destinationPath}");
            SetCurrentOperationDrive(GetDriveLetter(outputFolder)); // Monitor output drive for move write
            await Task.Run(() => File.Move(localTempIsoPath, destinationPath, true), _cts.Token);

            if (deleteOriginalIsoFile && !isTemporaryFileFromArchive)
            {
                _logger.LogMessage($"{logPrefix} Deleting original file as requested.");
                await TryDeleteFileAsync(inputFile);
            }
            else if (isTemporaryFileFromArchive)
            {
                // Original file was already temporary, it will be cleaned up by archive processing logic
                _logger.LogMessage($"{logPrefix} Original file was temporary (from archive), it will be cleaned up by archive logic.");
            }
            else
            {
                _logger.LogMessage($"{logPrefix} Original file kept as requested.");
            }

            return FileProcessingStatus.Converted;
        }
        catch (OperationCanceledException)
        {
            _logger.LogMessage($"{logPrefix} Operation canceled during processing.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"{logPrefix} Error processing: {ex.Message}");
            _ = ReportBugAsync($"Error processing file: {originalFileName}", ex);
            return FileProcessingStatus.Failed;
        }
        finally
        {
            // Clean up the local temporary working directory and its contents
            if (!string.IsNullOrEmpty(localTempWorkingDir))
            {
                try
                {
                    if (await Task.Run(() => Directory.Exists(localTempWorkingDir), CancellationToken.None))
                    {
                        await Task.Run(() => Directory.Delete(localTempWorkingDir, true), CancellationToken.None);
                        _logger.LogMessage($"{logPrefix} Cleaned up local temporary working directory: {localTempWorkingDir}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogMessage($"{logPrefix} Error cleaning up local temporary working directory {localTempWorkingDir}: {ex.Message}");
                }
            }
        }
    }

    private async Task<ConversionToolResultStatus> RunConversionToolAsync(string extractXisoPath, string inputFile, string originalFileName, bool skipSystemUpdate) // Add skipSystemUpdate parameter
    {
        var simpleFileName = Path.GetFileName(inputFile);
        var arguments = $"-r \"{inputFile}\"";

        // If skipSystemUpdate is true, prepend the -s option
        if (skipSystemUpdate)
        {
            arguments = $"-s {arguments}"; // This will result in "-s -r \"{inputFile}\""
        }

        _logger.LogMessage($"Running extract-xiso {arguments} on simple filename: {simpleFileName} (original: {originalFileName})");

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
                Arguments = arguments, // Use the constructed arguments string
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
                    // Ignore
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
                _logger.LogMessage($"Output from extract-xiso -r for {originalFileName}:\n{collectedToolOutputForLog}");
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
                    _logger.LogMessage($"extract-xiso -r for {originalFileName} exited with 1 but no 'skipped' message. Treating as failure.");
                    _ = ReportBugAsync($"extract-xiso -r for {originalFileName} exited 1 without skip message. Output: {string.Join(Environment.NewLine, localProcessOutputLines)}");
                    return ConversionToolResultStatus.Failed;
                default:
                    _ = ReportBugAsync($"extract-xiso -r failed for {originalFileName} with exit code {process.ExitCode}. Output: {string.Join(Environment.NewLine, localProcessOutputLines)}");
                    return ConversionToolResultStatus.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogMessage($"extract-xiso -r operation for {originalFileName} was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error running extract-xiso -r for {originalFileName}: {ex.Message}");
            _ = ReportBugAsync($"Exception during extract-xiso -r for {originalFileName}", ex);
            return ConversionToolResultStatus.Failed;
        }
        finally
        {
            await cancellationRegistration.DisposeAsync();
        }
    }

    private async Task CleanupTempFoldersAsync(List<string> tempFolders)
    {
        if (tempFolders.Count == 0) return;

        _logger.LogMessage("Cleaning up remaining temporary extraction folders...");
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
                _logger.LogMessage($"Error cleaning temp folder {folder}: {ex.Message}");
            }
        }

        if (tempFolders.Count == 0) _logger.LogMessage("Temporary folder cleanup complete.");
        else _logger.LogMessage("Some temporary folders could not be cleaned automatically.");
    }

    private async Task TryDeleteFileAsync(string filePath)
    {
        try
        {
            if (!await Task.Run(() => File.Exists(filePath), _cts.Token)) return;

            await Task.Run(() => File.Delete(filePath), _cts.Token);
            _logger.LogMessage($"Deleted: {Path.GetFileName(filePath)}");
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error deleting file {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    private void LogOperationSummary(string operationType)
    {
        _logger.LogMessage("");
        _logger.LogMessage($"--- Batch {operationType.ToLowerInvariant()} completed. ---");
        _logger.LogMessage($"Total files processed: {_uiTotalFiles}");
        _logger.LogMessage($"Successfully {GetPastTense(operationType)}: {_uiSuccessCount} files");
        _logger.LogMessage($"Skipped: {_uiSkippedCount} files");
        if (_uiFailedCount > 0) _logger.LogMessage($"Failed to {operationType.ToLowerInvariant()}: {_uiFailedCount} files");

        Application.Current.Dispatcher.InvokeAsync(() =>
            _messageBoxService.Show($"Batch {operationType.ToLowerInvariant()} completed.\n\n" +
                                    $"Total files processed: {_uiTotalFiles}\n" +
                                    $"Successfully {GetPastTense(operationType)}: {_uiSuccessCount} files\n" +
                                    $"Skipped: {_uiSkippedCount} files\n" +
                                    $"Failed: {_uiFailedCount} files",
                $"{operationType} Complete", MessageBoxButton.OK,
                _uiFailedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information));
    }

    private static string GetPastTense(string verb)
    {
        return verb.ToLowerInvariant() switch
        {
            "conversion" => "converted",
            "test" => "tested",
            _ => verb.ToLowerInvariant() + "ed"
        };
    }

    public async Task ReportBugAsync(string message, Exception? exception = null)
    {
        try
        {
            var fullReport = new StringBuilder();
            fullReport.AppendLine("=== Bug Report ===");
            fullReport.AppendLine($"Application: {App.ApplicationName}");
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
                    _urlOpener.OpenUrl(downloadUrl);
                }
            }
        }
        catch (Exception ex)
        {
            // Log and report the error, but don't bother the user.
            _logger.LogMessage($"Error checking for updates: {ex.Message}");
            _ = ReportBugAsync("Error during update check", ex);
        }
    }


    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var aboutWindow = _serviceProvider.GetRequiredService<AboutWindow>();
            aboutWindow.Owner = this;
            aboutWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error opening About window: {ex.Message}");
            _ = ReportBugAsync("Error opening About window", ex);
        }
    }

    public void Dispose()
    {
        _processingTimer.Tick -= ProcessingTimer_Tick;
        _processingTimer.Stop();
        StopPerformanceCounter();
        _cts?.Cancel();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
