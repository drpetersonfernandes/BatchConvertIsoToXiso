using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Threading;
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

    private int _invalidIsoErrorCount;
    private int _totalProcessedFiles;
    private readonly List<string> _failedConversionFilePaths = new();

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
        _ = CheckForUpdatesAsync();
    }

    private void UpdateStatus(string status)
    {
        StatusTextBlock.Text = status;
    }

    /// <summary>
    /// Checks if the selected path is the system's temporary directory or a subfolder within it.
    /// </summary>
    /// <param name="selectedPath">The path selected by the user.</param>
    /// <returns>True if the path is the system temp folder or a subfolder, false otherwise.</returns>
    private bool IsSystemTempPath(string selectedPath)
    {
        var systemTempPath = Path.GetTempPath();

        // Normalize both paths to ensure consistent comparison (e.g., handle trailing slashes)
        var normalizedSystemTempPath = Path.GetFullPath(systemTempPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedSelectedPath = Path.GetFullPath(selectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Check if the selected path is exactly the system temp path or starts with it (indicating a subfolder)
        return normalizedSelectedPath.Equals(normalizedSystemTempPath, StringComparison.OrdinalIgnoreCase) ||
               normalizedSelectedPath.StartsWith(normalizedSystemTempPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void BrowseConversionInputButton_Click(object sender, RoutedEventArgs e)
    {
        var inputFolder = SelectFolder("Select the folder containing ISO or archive files");
        if (string.IsNullOrEmpty(inputFolder)) return;

        if (IsSystemTempPath(inputFolder))
        {
            _messageBoxService.ShowError("The system's temporary folder or a subfolder within it cannot be selected as an input folder. Please choose a different location.");
            _logger.LogMessage($"Attempted to select system temp folder '{inputFolder}' as conversion input. Blocked.");
            return;
        }

        ConversionInputFolderTextBox.Text = inputFolder;
        _logger.LogMessage($"Conversion input folder selected: {inputFolder}");
    }

    private void BrowseConversionOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var outputFolder = SelectFolder("Select the output folder for converted XISO files");
        if (string.IsNullOrEmpty(outputFolder)) return;

        if (IsSystemTempPath(outputFolder))
        {
            _messageBoxService.ShowError("The system's temporary folder or a subfolder within it cannot be selected as an output folder. Please choose a different location.");
            _logger.LogMessage($"Attempted to select system temp folder '{outputFolder}' as conversion output. Blocked.");
            return;
        }

        ConversionOutputFolderTextBox.Text = outputFolder;
        _logger.LogMessage($"Conversion output folder selected: {outputFolder}");
    }

    private void BrowseTestInputButton_Click(object sender, RoutedEventArgs e)
    {
        var inputFolder = SelectFolder("Select the folder containing ISO files to test");
        if (string.IsNullOrEmpty(inputFolder)) return;

        if (IsSystemTempPath(inputFolder))
        {
            _messageBoxService.ShowError("The system's temporary folder or a subfolder within it cannot be selected as an input folder for testing. Please choose a different location.");
            _logger.LogMessage($"Attempted to select system temp folder '{inputFolder}' as test input. Blocked.");
            return;
        }

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
                LogViewer.Clear();

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
                var searchSubfolders = SearchSubfoldersConversionCheckBox.IsChecked ?? false;

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
                var tempPath = Path.GetTempPath(); // This is the base temp path, not the specific working dir
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

                // Calculate required temporary space dynamically based on the largest single item being processed
                List<string> topLevelEntries;
                try
                {
                    var searchOption = searchSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    topLevelEntries = await Task.Run(() => Directory.GetFiles(inputFolder, "*.*", searchOption)
                            .Where(static f =>
                            {
                                var ext = Path.GetExtension(f).ToLowerInvariant();
                                return ext is ".iso" or ".zip" or ".7z" or ".rar" or ".cue";
                            }).ToList(),
                        _cts.Token);

                    if (topLevelEntries.Count == 0)
                    {
                        _logger.LogMessage("No compatible files found in the input folder for conversion.");
                        var subfolderHint = searchSubfolders ? "" : " Please note this tool is currently configured not to search in subfolders.";
                        _messageBoxService.ShowError($"No compatible files (.iso, .zip, .7z, .rar, .cue) were found in the selected folder.{subfolderHint}");
                        SetControlsState(true);
                        _isOperationRunning = false;
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

                var maxTempSpaceNeededForConversion = await CalculateMaxTempSpaceForSingleOperation(topLevelEntries, true);
                if (tempDriveInfo.AvailableFreeSpace < maxTempSpaceNeededForConversion)
                {
                    _messageBoxService.ShowError($"Insufficient free space on temporary drive ({tempDriveLetter}). " +
                                                 $"Required (estimated for largest item): {Formatter.FormatBytes(maxTempSpaceNeededForConversion)}, Available: {Formatter.FormatBytes(tempDriveInfo.AvailableFreeSpace)}. " +
                                                 "Please free up space or choose a different temporary drive if possible (via system settings).");
                    StopPerformanceCounter();
                    _ = ReportBugAsync($"Insufficient temp space on drive {tempDriveLetter} for conversion. Available: {tempDriveInfo.AvailableFreeSpace}, Required: {maxTempSpaceNeededForConversion}");
                    return;
                }

                _logger.LogMessage($"INFO: Temporary drive '{tempDriveLetter}' has {Formatter.FormatBytes(tempDriveInfo.AvailableFreeSpace)} free space (required for conversion: {Formatter.FormatBytes(maxTempSpaceNeededForConversion)}).");

                // --- Count total processable files ---


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
                _logger.LogMessage($"Search in subfolders: {searchSubfolders}");
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
    }

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
                var searchSubfolders = SearchSubfoldersTestCheckBox.IsChecked ?? false;

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

                // Scan the input folder to get .iso files for size calculation and count
                List<string> isoFilesToTest;
                try
                {
                    var searchOption = searchSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    isoFilesToTest = await Task.Run(() => Directory.GetFiles(inputFolder, "*.iso", searchOption).ToList(), _cts.Token);
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
                    _messageBoxService.ShowError($"No .iso files were found in the selected folder for testing. {(searchSubfolders ? "" : "Please note this tool is currently configured not to search in subfolders.")}");
                    SetControlsState(true);
                    _isOperationRunning = false;
                    return;
                }

                // Calculate required temporary space dynamically based on the largest ISO being tested
                var maxTempSpaceNeededForTest = await CalculateMaxTempSpaceForSingleOperation(isoFilesToTest, false);
                if (tempDriveInfo.AvailableFreeSpace < maxTempSpaceNeededForTest)
                {
                    _messageBoxService.ShowError($"Insufficient free space on temporary drive ({tempDriveLetter}). " +
                                                 $"Required (estimated for largest item): {Formatter.FormatBytes(maxTempSpaceNeededForTest)}, Available: {Formatter.FormatBytes(tempDriveInfo.AvailableFreeSpace)}. " +
                                                 "Please free up space or choose a different temporary drive if possible (via system settings).");
                    StopPerformanceCounter();
                    _ = ReportBugAsync($"Insufficient temp space on drive {tempDriveLetter} for ISO test. Available: {tempDriveInfo.AvailableFreeSpace}, Required: {maxTempSpaceNeededForTest}");
                    return;
                }

                _logger.LogMessage($"INFO: Temporary drive '{tempDriveLetter}' has {Formatter.FormatBytes(tempDriveInfo.AvailableFreeSpace)} free space (required: {Formatter.FormatBytes(maxTempSpaceNeededForTest)}).");


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
                _logger.LogMessage($"Search in subfolders: {searchSubfolders}");
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
        SearchSubfoldersConversionCheckBox.IsEnabled = enabled;
        DeleteOriginalsCheckBox.IsEnabled = enabled;
        SkipSystemUpdateCheckBox.IsEnabled = enabled;
        StartConversionButton.IsEnabled = enabled;

        // Test Tab
        TestInputFolderTextBox.IsEnabled = enabled;
        BrowseTestInputButton.IsEnabled = enabled;
        MoveSuccessFilesCheckBox.IsEnabled = enabled;
        SearchSubfoldersTestCheckBox.IsEnabled = enabled;
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
                _messageBoxService.ShowWarning($"Many files ({_invalidIsoErrorCount} out of {_totalProcessedFiles}) were not valid Xbox ISOs. " +
                                               "Please ensure you are selecting the correct ISO files from Xbox or Xbox 360 games, " +
                                               "not from other consoles like PlayStation.", "High Rate of Invalid ISOs Detected");
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

    /// <summary>
    /// Calculates the maximum temporary disk space required for a single item (ISO, CUE/BIN, or archive)
    /// during either a conversion or test operation, including a buffer.
    /// </summary>
    /// <param name="filePaths">List of file paths to consider.</param>
    /// <param name="isConversionOperation">True if calculating for conversion, false for testing.</param>
    /// <returns>The estimated maximum required temporary space in bytes.</returns>
    private async Task<long> CalculateMaxTempSpaceForSingleOperation(List<string> filePaths, bool isConversionOperation)
    {
        long maxRequiredSpace = 0;
        foreach (var filePath in filePaths)
        {
            _cts.Token.ThrowIfCancellationRequested();
            try
            {
                var fileExtension = Path.GetExtension(filePath).ToLowerInvariant();
                long currentItemSize = 0;

                if (fileExtension == ".iso")
                {
                    currentItemSize = await Task.Run(() => new FileInfo(filePath).Length);
                }
                else if (isConversionOperation && fileExtension == ".cue")
                {
                    // For CUE, we need space for the resulting ISO. Assume it's roughly the size of the largest BIN.
                    var binPath = await ParseCueForBinFileAsync(filePath);
                    if (!string.IsNullOrEmpty(binPath)) // ParseCueForBinFileAsync already checks File.Exists
                    {
                        currentItemSize = await Task.Run(() => new FileInfo(binPath).Length);
                    }
                }
                else if (isConversionOperation && (fileExtension == ".zip" || fileExtension == ".7z" || fileExtension == ".rar"))
                {
                    // For archives, we need space for the extracted contents.
                    try
                    {
                        currentItemSize = await _fileExtractor.GetUncompressedArchiveSizeAsync(filePath, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogMessage($"Warning: Could not get uncompressed size of archive {Path.GetFileName(filePath)} for disk space calculation: {ex.Message}. Using heuristic.");
                        currentItemSize = new FileInfo(filePath).Length * 3; // Fallback heuristic: 3x compressed size
                    }
                }

                maxRequiredSpace = Math.Max(maxRequiredSpace, currentItemSize);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Warning: Could not get size of file {Path.GetFileName(filePath)} for disk space calculation: {ex.Message}");
            }
        }

        return isConversionOperation ? (long)(maxRequiredSpace * 1.5) : (long)(maxRequiredSpace * 2.1);
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

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _processingTimer.Tick -= ProcessingTimer_Tick;
        _processingTimer.Stop();
        StopPerformanceCounter();
        _cts?.Cancel();
        base.OnClosing(e);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}