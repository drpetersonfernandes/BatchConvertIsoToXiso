using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Threading;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
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
    private bool _isOperationRunning;

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

        Loaded += async (s, e) =>
        {
            DisplayInitialInstructions();
            await CheckForUpdatesAsync();
        };
    }

    private void DisplayInitialInstructions()
    {
        _logger.LogMessage("Welcome to the Batch Convert ISO to XISO & Test Tool.");
        _logger.LogMessage("");
        _logger.LogMessage("This application provides two main functions, available in the tabs above:");
        _logger.LogMessage("1. Convert to XISO: Converts standard Xbox ISO files to the optimized XISO format. It can also process ISOs found within .zip, .7z, and .rar archives.");
        _logger.LogMessage("2. Test ISO Integrity: Verifies the integrity of your .iso files by attempting a full extraction to a temporary location.");
        _logger.LogMessage("");
        _logger.LogMessage("IMPORTANT: This tool ONLY works with Xbox and Xbox 360 ISO files.");
        _logger.LogMessage("It cannot convert or test ISOs from PlayStation, PlayStation 2, or other consoles.");
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

        var bchunkPath = Path.Combine(appDirectory, "bchunk.exe");
        if (File.Exists(bchunkPath))
        {
            _logger.LogMessage("INFO: bchunk.exe found. CUE/BIN conversion is enabled.");
        }
        else
        {
            _logger.LogMessage("WARNING: bchunk.exe not found. CUE/BIN conversion will fail.");
            _ = ReportBugAsync("bchunk.exe not found.");
        }

        _logger.LogMessage("INFO: Archive extraction uses the SevenZipExtractor library.");
        _logger.LogMessage("--- Ready ---");
    }

    private void UpdateStatus(string status)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusTextBlock.Text = status;
        });
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
            _diskWriteSpeedCounter.NextValue(); // Initial call to prime the counter, ignore the first value
            // Second call to get a valid initial value, though it might still be 0
            _diskWriteSpeedCounter.NextValue();
            _activeMonitoringDriveLetter = driveLetter;
            _logger.LogMessage($"Monitoring write speed for drive: {perfCounterInstanceName}");
            Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "Calculating...");
        }
        catch (InvalidOperationException ex)
        {
            // This catch block should now primarily handle issues during counter-creation/access after existence checks.
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
        finally
        {
            Application.Current.Dispatcher.Invoke(() => WriteSpeedDriveIndicator.Text = _activeMonitoringDriveLetter != null ? $"({_activeMonitoringDriveLetter})" : "");
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

            if (WriteSpeedDriveIndicator != null)
            {
                WriteSpeedDriveIndicator.Text = "";
            }
        });
    }

    // Fix 2: Move cleanup logic from Dispose() to Window_Closing
    private void Window_Closing(object sender, CancelEventArgs e)
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CancellationTokenSource was already disposed, which is fine
        }

        _processingTimer.Tick -= ProcessingTimer_Tick;
        _processingTimer.Stop();
        StopPerformanceCounter();
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
            if (_isOperationRunning) return; // Prevent multiple clicks

            _isOperationRunning = true; // Set immediately
            SetControlsState(false); // Disable controls immediately
            try
            {
                Application.Current.Dispatcher.Invoke(() => LogViewer.Clear());

                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");

                if (!File.Exists(extractXisoPath))
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

                _logger.LogMessage($"INFO: Temporary drive '{tempDriveLetter}' has {Formatter.FormatBytes(tempDriveInfo.AvailableFreeSpace)} free space (required for conversion: {Formatter.FormatBytes(MinimumRequiredConversionTempSpaceBytes)}).");

                // --- Dry run to count total processable ISOs/CUEs ---
                // Scan the input folder to get initial entries for size calculation
                List<string> topLevelEntries;
                try
                {
                    topLevelEntries = await Task.Run(() =>
                            Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
                                .Where(static f =>
                                {
                                    var ext = Path.GetExtension(f).ToLowerInvariant();
                                    return ext is ".iso" or ".zip" or ".7z" or ".rar" or ".cue";
                                }).ToList(),
                        _cts.Token);

                    if (topLevelEntries.Count == 0)
                    {
                        _logger.LogMessage("No ISO files or supported archives found in the input folder for conversion.");
                        StopPerformanceCounter();
                        return;
                    }

                    // Set the total file count for the progress bar to the number of top-level entries.
                    _uiTotalFiles = topLevelEntries.Count;
                    _logger.LogMessage($"Scan complete. Found {_uiTotalFiles} top-level files/archives to process.");
                }
                catch (Exception ex)
                {
                    _logger.LogMessage($"Error scanning input folder for conversion: {ex.Message}");
                    _ = ReportBugAsync("Error scanning input folder", ex);
                    StopPerformanceCounter();
                    return;
                }

                // Calculate the total size of input files for output drive space check
                var totalInputSize = await CalculateTotalInputFileSizeAsync(topLevelEntries);
                var requiredOutputSpace = (long)(totalInputSize * 1.1); // 10% buffer for potential slight size increase or temporary files
                if (!CheckDriveSpace(outputFolder, requiredOutputSpace, "output"))
                {
                    StopPerformanceCounter();
                    return;
                }

                ResetSummaryStats();

                // Start with the output drive but allow dynamic switching
                var outputDrive = GetDriveLetter(outputFolder);
                _currentOperationDrive = outputDrive;
                InitializePerformanceCounter(outputDrive);

                _operationStartTime = DateTime.Now;
                _processingTimer.Start();

                UpdateStatus("Starting batch conversion...");
                _logger.LogMessage("--- Starting batch conversion process... ---");
                _logger.LogMessage($"Input folder: {inputFolder}");
                _logger.LogMessage($"Output folder: {outputFolder} (Estimated required space: {Formatter.FormatBytes(requiredOutputSpace)})");
                _logger.LogMessage($"Skip $SystemUpdate folder: {skipSystemUpdate}. Delete originals: {deleteFiles}");

                try
                {
                    await PerformBatchConversionAsync(extractXisoPath, inputFolder, outputFolder, deleteFiles, skipSystemUpdate, topLevelEntries);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogMessage("Operation was canceled by user during initial setup.");
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
                    Application.Current.Dispatcher.Invoke(() => ProcessingTimeValue.Text = finalElapsedTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
                    SetControlsState(true);
                    UpdateStatus("Conversion complete. Ready.");
                    _isOperationRunning = false;
                }
            }
            catch (Exception ex)
            {
                _ = ReportBugAsync($"Error during batch conversion process: {ex.Message}", ex);
                _logger.LogMessage($"Error during batch conversion process: {ex.Message}");
                StopPerformanceCounter();
                _isOperationRunning = false;
                LogOperationSummary("Conversion");
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync($"Error during batch conversion process: {ex.Message}", ex);
        }
        finally
        {
            LogOperationSummary("Conversion");
        } // Ensure summary is always logged
    } // End StartConversionButton_Click

    private async void StartTestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isOperationRunning) return;

            _isOperationRunning = true;
            SetControlsState(false); // Disable controls immediately
            try
            {
                Application.Current.Dispatcher.Invoke(() => LogViewer.Clear());

                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");

                if (!File.Exists(extractXisoPath))
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

                // Scan the input folder to get .iso files for size calculation and count
                List<string> isoFilesToTest;
                try
                {
                    isoFilesToTest = await Task.Run(() => Directory.GetFiles(inputFolder, "*.iso", SearchOption.TopDirectoryOnly).ToList(), _cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogMessage($"Error scanning input folder for .iso files: {ex.Message}");
                    _ = ReportBugAsync("Error scanning input folder for .iso files for testing", ex);
                    StopPerformanceCounter();
                    return;
                }

                if (isoFilesToTest.Count == 0)
                {
                    _logger.LogMessage("No .iso files found in the input folder for testing.");
                    StopPerformanceCounter();
                    return;
                }

                var totalIsoSize = await CalculateTotalInputFileSizeAsync(isoFilesToTest);

                // Check destination drive space for moving files (if cross-drive move)
                if (moveSuccessful || moveFailed)
                {
                    var inputDriveLetter = GetDriveLetter(inputFolder);
                    var checkedDrives = new HashSet<string>();

                    if (moveSuccessful && successFolder != null)
                    {
                        var successDriveLetter = GetDriveLetter(successFolder);
                        if (successDriveLetter != null && !successDriveLetter.Equals(inputDriveLetter, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!CheckDriveSpace(successFolder, totalIsoSize, "success destination"))
                            {
                                StopPerformanceCounter();
                                return;
                            }

                            checkedDrives.Add(successDriveLetter);
                        }
                    }

                    if (moveFailed && failedFolder != null)
                    {
                        var failedDriveLetter = GetDriveLetter(failedFolder);
                        if (failedDriveLetter != null && !failedDriveLetter.Equals(inputDriveLetter, StringComparison.OrdinalIgnoreCase) && !checkedDrives.Contains(failedDriveLetter))
                        {
                            if (!CheckDriveSpace(failedFolder, totalIsoSize, "failed destination"))
                            {
                                StopPerformanceCounter();
                                return;
                            }
                        }
                    }
                }

                ResetSummaryStats();
                _uiTotalFiles = isoFilesToTest.Count; // Set total files for testing
                UpdateSummaryStatsUi();

                // Initial drive for monitoring is the temp path, as the test involves extraction to temp.
                var initialDriveForMonitoring = GetDriveLetter(Path.GetTempPath());
                _currentOperationDrive = initialDriveForMonitoring; // Initial drive
                InitializePerformanceCounter(initialDriveForMonitoring);

                _operationStartTime = DateTime.Now;
                _processingTimer.Start();

                UpdateStatus("Starting batch ISO test...");
                _logger.LogMessage("--- Starting batch ISO test process... ---");
                _logger.LogMessage($"Input folder: {inputFolder}");
                _logger.LogMessage($"Total ISOs to test: {isoFilesToTest.Count}. Total size: {Formatter.FormatBytes(totalIsoSize)}");
                if (moveSuccessful) _logger.LogMessage($"Moving successful files to: {successFolder}"); // This is a subfolder, so it's fine.
                if (moveFailed) _logger.LogMessage($"Moving failed files to: {failedFolder}");

                try
                {
                    await PerformBatchIsoTestAsync(extractXisoPath, inputFolder, moveSuccessful, successFolder, moveFailed, failedFolder, isoFilesToTest);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogMessage("Operation was canceled by user during initial setup.");
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
                    Application.Current.Dispatcher.Invoke(() => ProcessingTimeValue.Text = finalElapsedTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
                    SetControlsState(true);
                    UpdateStatus("Test complete. Ready.");
                    _isOperationRunning = false;
                }
            }
            catch (Exception ex)
            {
                _ = ReportBugAsync($"Error during batch ISO test process: {ex.Message}", ex);
                _logger.LogMessage($"Error during batch ISO test process: {ex.Message}");
                StopPerformanceCounter();
                _isOperationRunning = false;
                LogOperationSummary("Test");
            }
            finally
            {
                LogOperationSummary("Test");
            } // Ensure summary is always logged
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync($"Error during batch ISO test process: {ex.Message}", ex);
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

    // Add these fields to track invalid ISO errors
    private int _invalidIsoErrorCount;
    private int _totalProcessedFiles;
    private readonly List<string> _failedConversionFilePaths = new(); // Track original paths of failed items

    private void ResetSummaryStats()
    {
        _uiTotalFiles = 0;
        _uiSuccessCount = 0;
        _uiFailedCount = 0;
        _uiSkippedCount = 0;
        _invalidIsoErrorCount = 0; // Reset invalid ISO count
        _totalProcessedFiles = 0; // Reset total processed count
        _failedConversionFilePaths.Clear(); // Clear failed paths
        UpdateSummaryStatsUi();
        UpdateProgressUi(0, 0);
        ProcessingTimeValue.Text = "00:00:00";
        if (!_processingTimer.IsEnabled)
        {
            StopPerformanceCounter();
        }
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

    private void UpdateSummaryStatsUi()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            TotalFilesValue.Text = _uiTotalFiles.ToString(CultureInfo.InvariantCulture);
            SuccessValue.Text = _uiSuccessCount.ToString(CultureInfo.InvariantCulture);
            FailedValue.Text = _uiFailedCount.ToString(CultureInfo.InvariantCulture);
            SkippedValue.Text = _uiSkippedCount.ToString(CultureInfo.InvariantCulture);
        });
    }

    // This method is now only for updating the progress bar, not the total files count.
    // The total files count (_uiTotalFiles) is set once at the beginning by CountTotalIsosAndCuesInFolderAsync.
    // The `current` parameter will be `actualIsosProcessedForProgress`.
    // The `total` parameter will be `_uiTotalFiles`.
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

    private async Task PerformBatchConversionAsync(string extractXisoPath, string inputFolder, string outputFolder, bool deleteOriginals, bool skipSystemUpdate, List<string> topLevelEntries) // Renamed initialEntriesToProcess to topLevelEntries
    {
        var tempFoldersToCleanUpAtEnd = new List<string>();
        var archivesFailedToExtractOrProcess = 0;
        var topLevelEntriesProcessed = 0;

        // _uiTotalFiles is now the count of topLevelEntries
        UpdateSummaryStatsUi(); // Ensure UI reflects the initial _uiTotalFiles
        UpdateProgressUi(0, _uiTotalFiles);
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
        _logger.LogMessage($"Starting conversion... Total top-level files/archives to process: {_uiTotalFiles}.");

        var globalFileIndex = 1; // Global counter for simple filenames

        foreach (var currentEntryPath in topLevelEntries)
        {
            // --- Increment _totalProcessedFiles here for each actual ISO/CUE processed ---
            // This counter will be incremented for each ISO or CUE file that is *attempted* to be converted.
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
                    _totalProcessedFiles++; // Increment for each ISO processed
                    globalFileIndex++;
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
                            _failedConversionFilePaths.Add(currentEntryPath);
                            break;
                    }

                    UpdateSummaryStatsUi();
                    break;
                }
                case ".zip" or ".7z" or ".rar":
                {
                    _logger.LogMessage($"Processing archive: {entryFileName}...");
                    string? currentArchiveTempExtractionDir = null;
                    var archiveExtractedSuccessfully = false;

                    try
                    {
                        currentArchiveTempExtractionDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_Extract", Guid.NewGuid().ToString());
                        await Task.Run(() => Directory.CreateDirectory(currentArchiveTempExtractionDir));
                        tempFoldersToCleanUpAtEnd.Add(currentArchiveTempExtractionDir);
                        SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath()));
                        archiveExtractedSuccessfully = await _fileExtractor.ExtractArchiveAsync(currentEntryPath, currentArchiveTempExtractionDir, _cts);
                        if (archiveExtractedSuccessfully)
                        {
                            var extractedIsoFiles = await Task.Run(() => Directory.GetFiles(currentArchiveTempExtractionDir, "*.iso", SearchOption.AllDirectories), _cts.Token);
                            var extractedCueFiles = await Task.Run(() => Directory.GetFiles(currentArchiveTempExtractionDir, "*.cue", SearchOption.AllDirectories), _cts.Token);
                            var totalFoundFiles = extractedIsoFiles.Length + extractedCueFiles.Length;

                            if (totalFoundFiles == 0)
                            {
                                _logger.LogMessage($"No ISO or CUE files found in archive: {entryFileName}. Skipping archive.");
                            }

                            _logger.LogMessage($"Found {extractedIsoFiles.Length} ISO(s) and {extractedCueFiles.Length} CUE file(s) in {entryFileName}. Processing them now...");

                            foreach (var extractedIsoPath in extractedIsoFiles)
                            {
                                _cts.Token.ThrowIfCancellationRequested();
                                var extractedIsoName = Path.GetFileName(extractedIsoPath);
                                _logger.LogMessage($"  Converting ISO from archive: {extractedIsoName}...");
                                var status = await ConvertFileAsync(extractXisoPath, extractedIsoPath, outputFolder, false, globalFileIndex, skipSystemUpdate);
                                _totalProcessedFiles++; // Increment for each ISO processed from the archive
                                globalFileIndex++;
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
                                        _failedConversionFilePaths.Add(extractedIsoPath);
                                        break;
                                }

                                UpdateSummaryStatsUi();
                            }

                            foreach (var extractedCuePath in extractedCueFiles)
                            {
                                _cts.Token.ThrowIfCancellationRequested();
                                var extractedCueName = Path.GetFileName(extractedCuePath);
                                _logger.LogMessage($"  Converting CUE/BIN from archive: {extractedCueName}...");
                                string? tempCueBinDir = null; // Local temp dir for bchunk output

                                try
                                {
                                    tempCueBinDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_CueBin", Guid.NewGuid().ToString());
                                    await Task.Run(() => Directory.CreateDirectory(tempCueBinDir));

                                    var tempIsoPath = await ConvertCueBinToIsoAsync(extractedCuePath, tempCueBinDir);
                                    if (tempIsoPath != null && await Task.Run(() => File.Exists(tempIsoPath)))
                                    {
                                        var status = await ConvertFileAsync(extractXisoPath, tempIsoPath, outputFolder, false, globalFileIndex, skipSystemUpdate);
                                        _totalProcessedFiles++; // Increment for each ISO processed from CUE/BIN
                                        globalFileIndex++;
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
                                                _failedConversionFilePaths.Add(extractedCuePath); // Log the original CUE path as the failure source
                                                break;
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogMessage($"Failed to convert CUE/BIN to ISO: {extractedCueName}. It will be skipped.");
                                        _uiFailedCount++;
                                        _failedConversionFilePaths.Add(extractedCuePath);
                                        _totalProcessedFiles++; // Increment for failed CUE/BIN conversion attempt
                                    }
                                }
                                finally
                                {
                                    if (tempCueBinDir != null) await CleanupTempFoldersAsync(new List<string> { tempCueBinDir });
                                    UpdateSummaryStatsUi();
                                }
                            }
                        }
                        else
                        {
                            _logger.LogMessage($"Failed to extract archive: {entryFileName}. It will be skipped.");
                            archivesFailedToExtractOrProcess++;
                            _totalProcessedFiles++; // Increment for the archive itself as a failed item
                            _uiFailedCount++;
                            _failedConversionFilePaths.Add(currentEntryPath);
                            UpdateSummaryStatsUi();
                        }

                        switch (deleteOriginals)
                        {
                            case true when archiveExtractedSuccessfully:
                            {
                                // Check if all ISOs/CUEs *from this archive* were successful or skipped.
                                // This is tricky because _uiSuccessCount and _uiSkippedCount are global.
                                // A more robust way would be to track statuses per archive, but for now,
                                // we'll assume if the archive extracted and we didn't add its original path to _failedConversionFilePaths, it's okay.
                                // This logic needs to be refined if we want precise per-archive deletion.
                                // For simplicity, if *any* ISO from the archive failed, we won't delete the archive.
                                if (!_failedConversionFilePaths.Contains(currentEntryPath)) // If the archive itself wasn't marked as failed
                                {
                                    _logger.LogMessage($"All contents of archive {entryFileName} processed successfully. Deleting original archive.");
                                    await TryDeleteFileAsync(currentEntryPath);
                                }
                                else
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
                        _failedConversionFilePaths.Add(currentEntryPath);
                        if (!archiveExtractedSuccessfully)
                        {
                            _totalProcessedFiles++; // Increment for the archive itself as a failed item
                            _uiFailedCount++;
                            UpdateSummaryStatsUi();
                        }
                    }
                    finally
                    {
                        if (currentArchiveTempExtractionDir != null && await Task.Run(() => Directory.Exists(currentArchiveTempExtractionDir)))
                        {
                            try
                            {
                                await Task.Run(() => Directory.Delete(currentArchiveTempExtractionDir, true));
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
                case ".cue":
                {
                    _logger.LogMessage($"Processing CUE/BIN: {entryFileName}...");
                    string? tempCueBinDir = null;

                    try
                    {
                        // Create a temp dir for bchunk output
                        tempCueBinDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_CueBin", Guid.NewGuid().ToString());
                        await Task.Run(() => Directory.CreateDirectory(tempCueBinDir));

                        // Run bchunk to convert CUE/BIN to ISO
                        var tempIsoPath = await ConvertCueBinToIsoAsync(currentEntryPath, tempCueBinDir);

                        if (tempIsoPath != null && await Task.Run(() => File.Exists(tempIsoPath)))
                        {
                            // Now, process the newly created ISO file
                            // We pass 'false' for deleteOriginalIsoFile because we handle deletion of cue/bin separately
                            var status = await ConvertFileAsync(extractXisoPath, tempIsoPath, outputFolder, false, globalFileIndex, skipSystemUpdate);
                            globalFileIndex++;
                            _totalProcessedFiles++; // Increment for CUE/BIN processed to ISO

                            switch (status)
                            {
                                case FileProcessingStatus.Converted:
                                    _uiSuccessCount++;
                                    if (deleteOriginals) await DeleteCueAndBinFilesAsync(currentEntryPath);
                                    break;
                                case FileProcessingStatus.Skipped:
                                    _uiSkippedCount++;
                                    if (deleteOriginals) await DeleteCueAndBinFilesAsync(currentEntryPath);
                                    break;
                                case FileProcessingStatus.Failed:
                                    _uiFailedCount++;
                                    _failedConversionFilePaths.Add(currentEntryPath);
                                    break;
                            }
                        }
                        else
                        {
                            _logger.LogMessage($"Failed to convert CUE/BIN to ISO: {entryFileName}. It will be skipped.");
                            _uiFailedCount++;
                            _failedConversionFilePaths.Add(currentEntryPath);
                            _totalProcessedFiles++; // Increment for failed CUE/BIN conversion attempt
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogMessage($"Error processing CUE/BIN {entryFileName}: {ex.Message}");
                        _ = ReportBugAsync($"Error during processing of CUE/BIN {entryFileName}", ex);
                        _uiFailedCount++;
                        _failedConversionFilePaths.Add(currentEntryPath);
                        _totalProcessedFiles++; // Increment for CUE/BIN processing error
                    }
                    finally
                    {
                        if (tempCueBinDir != null) await CleanupTempFoldersAsync(new List<string> { tempCueBinDir });
                        UpdateSummaryStatsUi();
                    }

                    break;
                }
            }

            topLevelEntriesProcessed++;
            UpdateProgressUi(topLevelEntriesProcessed, _uiTotalFiles);
        }

        if (!_cts.Token.IsCancellationRequested)
        {
            UpdateProgressUi(_uiTotalFiles, _uiTotalFiles);
        }

        if (archivesFailedToExtractOrProcess > 0)
        {
            _logger.LogMessage($"Note: {archivesFailedToExtractOrProcess} archive(s) failed to extract or had processing errors.");
        }

        if (_failedConversionFilePaths.Count > 0)
        {
            _logger.LogMessage("\nList of items that failed conversion or archive extraction:");
            foreach (var failedPath in _failedConversionFilePaths)
            {
                _logger.LogMessage($"- {Path.GetFileName(failedPath)} (Full path: {failedPath})");
            }
        }

        await CleanupTempFoldersAsync(tempFoldersToCleanUpAtEnd);
    }

    private async Task PerformBatchIsoTestAsync(string extractXisoPath, string inputFolder, bool moveSuccessful, string? successFolder, bool moveFailed, string? failedFolder, List<string> isoFilesToTest)
    {
        var actualIsosProcessedForProgress = 0;

        _uiSuccessCount = 0;
        _uiFailedCount = 0;
        _uiSkippedCount = 0;

        _logger.LogMessage($"Found {isoFilesToTest.Count} .iso files for testing.");
        UpdateSummaryStatsUi(); // Use _uiTotalFiles which is set to isoFilesToTest.Count
        UpdateProgressUi(0, _uiTotalFiles);
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
            SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath())); // Test extraction always uses the temp path

            var testStatus = await TestSingleIsoAsync(extractXisoPath, isoFilePath, testFileIndex);
            testFileIndex++;
            actualIsosProcessedForProgress++; // Increment for each ISO tested
            _totalProcessedFiles++; // Increment for each ISO tested

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
                _failedConversionFilePaths.Add(isoFilePath); // Use the general failed paths list
                _logger.LogMessage($"  FAILURE: '{isoFileName}' failed test.");

                if (moveFailed && !string.IsNullOrEmpty(failedFolder))
                {
                    SetCurrentOperationDrive(GetDriveLetter(failedFolder)); // Switch drive for move operation
                    await _fileMover.MoveTestedFileAsync(isoFilePath, failedFolder, "failed test", _cts.Token);
                }
            }

            UpdateSummaryStatsUi();
            UpdateProgressUi(actualIsosProcessedForProgress, _uiTotalFiles);
        }

        if (!_cts.Token.IsCancellationRequested && actualIsosProcessedForProgress >= _uiTotalFiles)
        {
            UpdateProgressUi(_uiTotalFiles, _uiTotalFiles);
        }

        if (_failedConversionFilePaths.Count > 0 && _uiFailedCount > 0)
        {
            _logger.LogMessage("\nList of ISOs that failed the test (original names):");
            foreach (var originalPath in _failedConversionFilePaths)
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
            await Task.Run(() => Directory.CreateDirectory(tempExtractionDir));
            _logger.LogMessage($"  Comprehensive Test for '{isoFileName}'");

            if (!File.Exists(isoFilePath))
            {
                _logger.LogMessage($"  ERROR: ISO file does not exist: {isoFilePath}");
                return IsoTestResultStatus.Failed;
            }

            try
            {
                var fileInfo = new FileInfo(isoFilePath);
                var length = await Task.Run(() => fileInfo.Length);
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

            // Always rename to a simple filename for testing
            var simpleFilename = GenerateSimpleFilename(fileIndex);
            simpleFilePath = Path.Combine(tempExtractionDir, simpleFilename);

            _logger.LogMessage($"  Copying '{isoFileName}' to simple filename '{simpleFilename}' for testing");
            await Task.Run(() => File.Copy(isoFilePath, simpleFilePath, true));

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
                if (Directory.Exists(tempExtractionDir))
                {
                    await Task.Run(() => Directory.Delete(tempExtractionDir, true));
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

    private async Task<FileProcessingStatus> ConvertFileAsync(string extractXisoPath, string inputFile, string outputFolder, bool deleteOriginalIsoFile, int fileIndex, bool skipSystemUpdate)
    {
        var originalFileName = Path.GetFileName(inputFile);
        var logPrefix = $"File '{originalFileName}':";
        string? localTempIsoPath; // Path to the ISO in the local temp directory
        string? localTempWorkingDir = null; // Directory for local temp operations

        try
        {
            // 1. Create a local temporary directory for this file's processing
            localTempWorkingDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_Convert", Guid.NewGuid().ToString());
            await Task.Run(() => Directory.CreateDirectory(localTempWorkingDir));
            _logger.LogMessage($"{logPrefix} Created local temporary working directory: {localTempWorkingDir}");

            // 2. Generate a simple filename for the local copy
            var simpleFilename = GenerateSimpleFilename(fileIndex);
            localTempIsoPath = Path.Combine(localTempWorkingDir, simpleFilename);

            // 3. Copy the original file from source (potentially UNC) to local temp
            _logger.LogMessage($"{logPrefix} Copying from '{inputFile}' to local temp '{localTempIsoPath}'...");
            SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath())); // Monitor temp drive for copy write
            await Task.Run(() => File.Copy(inputFile, localTempIsoPath, true));
            _logger.LogMessage($"{logPrefix} Successfully copied to local temp.");

            // 4. Run extract-xiso on the local temporary copy
            SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath())); // Still monitoring temp drive for in-place rewrite
            var toolResult = await RunConversionToolAsync(extractXisoPath, localTempIsoPath, originalFileName, skipSystemUpdate);

            var isTemporaryFileFromArchive = inputFile.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

            if (toolResult == ConversionToolResultStatus.Failed)
            {
                _logger.LogMessage($"{logPrefix} extract-xiso tool reported failure on local copy.");
                // No need to restore original filename as original is untouched.
                return FileProcessingStatus.Failed;
            }

            // Ensure output folder exists
            await Task.Run(() => Directory.CreateDirectory(outputFolder));
            var destinationPath = Path.Combine(outputFolder, originalFileName); // Use the original filename for destination

            if (toolResult == ConversionToolResultStatus.Skipped)
            {
                _logger.LogMessage($"{logPrefix} Already optimized. Moving local copy to output with original filename.");
                SetCurrentOperationDrive(GetDriveLetter(outputFolder)); // Monitor output drive for move write
                await Task.Run(() => File.Move(localTempIsoPath, destinationPath, true));

                if (deleteOriginalIsoFile && !isTemporaryFileFromArchive)
                {
                    _logger.LogMessage($"{logPrefix} Deleting original file as requested.");
                    await TryDeleteFileAsync(inputFile);
                }
                else if (isTemporaryFileFromArchive)
                {
                    // The original file was already temporary, it will be cleaned up by archive processing logic
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
            await Task.Run(() => File.Move(localTempIsoPath, destinationPath, true));

            if (deleteOriginalIsoFile && !isTemporaryFileFromArchive)
            {
                _logger.LogMessage($"{logPrefix} Deleting original file as requested.");
                await TryDeleteFileAsync(inputFile);
            }
            else if (isTemporaryFileFromArchive)
            {
                // The original file was already temporary, it will be cleaned up by archive processing logic
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
                    if (Directory.Exists(localTempWorkingDir))
                    {
                        await Task.Run(() => Directory.Delete(localTempWorkingDir, true));
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

    private async Task<ConversionToolResultStatus> RunConversionToolAsync(string extractXisoPath, string inputFile, string originalFileName, bool skipSystemUpdate)
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
                Arguments = arguments,
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
                    // Check for expected validation failures (non-Xbox ISOs)
                    var outputString = string.Join(Environment.NewLine, localProcessOutputLines);
                    if (outputString.Contains("does not appear to be a valid xbox iso image", StringComparison.OrdinalIgnoreCase) ||
                        outputString.Contains("failed to rewrite xbox iso image", StringComparison.OrdinalIgnoreCase) ||
                        outputString.Contains("read error: No error", StringComparison.OrdinalIgnoreCase))
                    {
                        // --- Increment _invalidIsoErrorCount only for these specific messages ---
                        _logger.LogMessage($"SKIPPED: '{originalFileName}' is not a valid Xbox ISO image. " +
                                           $"This file appears to be from a different console (e.g., PlayStation). " +
                                           $"Please ensure you are processing Xbox or Xbox 360 ISO files only.");
                        _invalidIsoErrorCount++; // Track invalid ISO errors
                        return ConversionToolResultStatus.Failed; // Don't send the bug report for expected errors
                    }

                    // Handle cases where exit code is 1 but the operation was successful.
                    if (localProcessOutputLines.Any(static line => line.Contains("successfully rewritten", StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogMessage($"extract-xiso -r for {originalFileName} exited with 1, but output indicates success ('successfully rewritten'). Treating as success.");
                        return ConversionToolResultStatus.Success;
                    }

                    _logger.LogMessage($"extract-xiso -r for {originalFileName} exited with 1 but no 'skipped' or 'success' message. Treating as failure.");
                    _ = ReportBugAsync($"extract-xiso -r for {originalFileName} exited 1 without skip/success message. Output: {string.Join(Environment.NewLine, localProcessOutputLines)}");
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
                    processRef?.Kill(true);
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

            var createdIso = await Task.Run(() => Directory.GetFiles(tempOutputDir, "*.iso").FirstOrDefault(), _cts.Token);
            if (createdIso == null)
            {
                _logger.LogMessage("  bchunk.exe finished, but no ISO file was found in the output directory.");
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

    private async Task CleanupTempFoldersAsync(List<string> tempFolders)
    {
        if (tempFolders.Count == 0) return;

        _logger.LogMessage("Cleaning up remaining temporary extraction folders...");
        foreach (var folder in tempFolders.ToList())
        {
            try
            {
                if (!Directory.Exists(folder)) continue;

                await Task.Run(() => Directory.Delete(folder, true));
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
            if (!File.Exists(filePath)) return;

            await Task.Run(() => File.Delete(filePath));
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
        {
            // Show warning if high rate of invalid ISOs detected
            if (_totalProcessedFiles > 5 && (double)_invalidIsoErrorCount / _totalProcessedFiles > 0.5)
            {
                _messageBoxService.ShowWarning(
                    $"Many files ({_invalidIsoErrorCount} out of {_totalProcessedFiles}) were not valid Xbox ISOs. " +
                    "Please ensure you are selecting the correct ISO files from Xbox or Xbox 360 games, " +
                    "not from other consoles like PlayStation.",
                    "High Rate of Invalid ISOs Detected");
            }

            _messageBoxService.Show($"Batch {operationType.ToLowerInvariant()} completed.\n\n" +
                                    $"Total files processed: {_uiTotalFiles}\n" +
                                    $"Successfully {GetPastTense(operationType)}: {_uiSuccessCount} files\n" +
                                    $"Skipped: {_uiSkippedCount} files\n" +
                                    $"Failed: {_uiFailedCount} files",
                $"{operationType} Complete", MessageBoxButton.OK,
                _uiFailedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        });
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

    private async Task<long> CalculateTotalInputFileSizeAsync(List<string> filePaths)
    {
        long totalSize = 0;
        foreach (var filePath in filePaths)
        {
            _cts.Token.ThrowIfCancellationRequested();
            try
            {
                totalSize += await Task.Run(() => new FileInfo(filePath).Length);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Warning: Could not get size of file {Path.GetFileName(filePath)} for disk space calculation: {ex.Message}");
                // Continue, but the totalSize might be underestimated.
            }
        }

        return totalSize;
    }

    private bool CheckDriveSpace(string path, long requiredSpace, string operationDescription)
    {
        var driveLetter = GetDriveLetter(path);
        if (string.IsNullOrEmpty(driveLetter))
        {
            _messageBoxService.ShowError($"Could not determine drive for {operationDescription} path '{path}'. Cannot proceed.");
            _ = ReportBugAsync($"Could not determine drive for {operationDescription} path '{path}'.");
            return false;
        }

        DriveInfo driveInfo;
        try
        {
            driveInfo = new DriveInfo(driveLetter);
        }
        catch (ArgumentException ex)
        {
            _messageBoxService.ShowError($"Invalid drive specified for {operationDescription} path '{path}': {ex.Message}. Cannot proceed.");
            _ = ReportBugAsync($"Invalid drive for {operationDescription} path '{path}'. Exception: {ex.Message}", ex);
            return false;
        }

        if (!driveInfo.IsReady)
        {
            _messageBoxService.ShowError($"{operationDescription} drive '{driveLetter}' is not ready. Cannot proceed.");
            _ = ReportBugAsync($"{operationDescription} drive '{driveLetter}' not ready.");
            return false;
        }

        if (driveInfo.AvailableFreeSpace < requiredSpace)
        {
            _messageBoxService.ShowError($"Insufficient free space on {operationDescription} drive ({driveLetter}). Required (estimated): {Formatter.FormatBytes(requiredSpace)}, Available: {Formatter.FormatBytes(driveInfo.AvailableFreeSpace)}. Please free up space.");
            _ = ReportBugAsync($"Insufficient space on {operationDescription} drive {driveLetter}. Available: {driveInfo.AvailableFreeSpace}, Required: {requiredSpace}");
            return false;
        }

        _logger.LogMessage($"INFO: {operationDescription} drive '{driveLetter}' has {Formatter.FormatBytes(driveInfo.AvailableFreeSpace)} free space (estimated required: {Formatter.FormatBytes(requiredSpace)}).");
        return true;
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
            // ignore
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

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}