using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Threading;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private CancellationTokenSource _cts = new();
    private readonly IUpdateChecker _updateChecker;
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly IFileExtractor _fileExtractor;
    private readonly IFileMover _fileMover;
    private readonly IUrlOpener _urlOpener;
    private readonly ISettingsService _settingsService;

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

    public MainWindow(IUpdateChecker updateChecker, ILogger logger, IBugReportService bugReportService,
        IMessageBoxService messageBoxService, IFileExtractor fileExtractor,
        IFileMover fileMover, IUrlOpener urlOpener, ISettingsService settingsService)
    {
        InitializeComponent();

        _updateChecker = updateChecker;
        _logger = logger;
        _bugReportService = bugReportService;
        _messageBoxService = messageBoxService;
        _fileExtractor = fileExtractor;
        _fileMover = fileMover;
        _urlOpener = urlOpener;
        _settingsService = settingsService;

        _logger.Initialize(LogViewer);

        LoadSettings();

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

    private void LoadSettings()
    {
        var settings = _settingsService.LoadSettings();

        ConversionInputFolderTextBox.Text = settings.ConversionInputFolder;
        ConversionOutputFolderTextBox.Text = settings.ConversionOutputFolder;
        TestInputFolderTextBox.Text = settings.TestInputFolder;
        DeleteOriginalsCheckBox.IsChecked = settings.DeleteOriginals;
        SearchSubfoldersConversionCheckBox.IsChecked = settings.SearchSubfoldersConversion;
        SkipSystemUpdateCheckBox.IsChecked = settings.SkipSystemUpdate;
        MoveSuccessFilesCheckBox.IsChecked = settings.MoveSuccessFiles;
        MoveFailedFilesCheckBox.IsChecked = settings.MoveFailedFiles;
        SearchSubfoldersTestCheckBox.IsChecked = settings.SearchSubfoldersTest;
    }

    private void SaveSettings()
    {
        var settings = new ApplicationSettings
        {
            ConversionInputFolder = ConversionInputFolderTextBox.Text,
            ConversionOutputFolder = ConversionOutputFolderTextBox.Text,
            TestInputFolder = TestInputFolderTextBox.Text,
            DeleteOriginals = DeleteOriginalsCheckBox.IsChecked ?? false,
            SearchSubfoldersConversion = SearchSubfoldersConversionCheckBox.IsChecked ?? false,
            SkipSystemUpdate = SkipSystemUpdateCheckBox.IsChecked ?? false,
            MoveSuccessFiles = MoveSuccessFilesCheckBox.IsChecked ?? false,
            MoveFailedFiles = MoveFailedFilesCheckBox.IsChecked ?? false,
            SearchSubfoldersTest = SearchSubfoldersTestCheckBox.IsChecked ?? false
        };

        _settingsService.SaveSettings(settings);
    }

    private async Task PreOperationCleanupAsync()
    {
        _logger.LogMessage("Performing pre-operation cleanup of temporary folders...");
        await TempFolderCleanupHelper.CleanupBatchConvertTempFoldersAsync(_logger);
        _logger.LogMessage("Pre-operation cleanup completed.");
    }

    private void BrowseConversionInputButton_Click(object sender, RoutedEventArgs e)
    {
        var inputFolder = SelectFolder("Select the folder containing ISO or archive files");
        if (string.IsNullOrEmpty(inputFolder)) return;

        if (CheckForTempPath.IsSystemTempPath(inputFolder))
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

        if (CheckForTempPath.IsSystemTempPath(outputFolder))
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

        if (CheckForTempPath.IsSystemTempPath(inputFolder))
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

            await PreOperationCleanupAsync();

            try
            {
                LogViewer.Clear();

                // Thread-safe cancellation token replacement
                var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
                oldCts?.Dispose();

                var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");

                if (!File.Exists(extractXisoPath))
                {
                    _logger.LogMessage("Error: extract-xiso.exe not found.");
                    _messageBoxService.ShowError("extract-xiso.exe is missing. Please ensure it's in the application folder.");
                    SetControlsState(true);
                    _isOperationRunning = false;
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
                    SetControlsState(true);
                    _isOperationRunning = false;
                    return;
                }

                if (!ValidateInputOutputFolders(inputFolder, outputFolder))
                {
                    SetControlsState(true);
                    _isOperationRunning = false; // Reset flag before early return
                    return;
                }

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
                }
                catch (Exception ex)
                {
                    _logger.LogMessage($"Error scanning input folder for conversion: {ex.Message}");
                    _ = ReportBugAsync("Error scanning input folder", ex);
                    SetControlsState(true);
                    _isOperationRunning = false;
                    return;
                }

                if (!await ValidateDiskSpaceAsync(topLevelEntries, outputFolder))
                {
                    SetControlsState(true);
                    _isOperationRunning = false;
                    return;
                }

                ResetSummaryStats();

                _uiTotalFiles = topLevelEntries.Count;
                UpdateSummaryStatsUi();

                // Start with the output drive but allow dynamic switching
                var outputDrive = GetDriveLetter(outputFolder);
                _currentOperationDrive = outputDrive;
                InitializePerformanceCounter(outputDrive);

                _operationStartTime = DateTime.Now;
                _processingTimer.Start();

                UpdateStatus("Starting batch conversion...");
                _logger.LogMessage("--- Starting batch conversion process... ---");
                _logger.LogMessage($"Found {topLevelEntries.Count} top-level files/archives to process.");
                _logger.LogMessage($"Input folder: {inputFolder}");
                _logger.LogMessage($"Output folder: {outputFolder}");
                _logger.LogMessage($"Search in subfolders: {searchSubfolders}");
                _logger.LogMessage($"Skip $SystemUpdate folder: {skipSystemUpdate}. Delete originals: {deleteFiles}");

                try
                {
                    await PerformBatchConversionAsync(extractXisoPath, inputFolder, outputFolder, deleteFiles, skipSystemUpdate, topLevelEntries);
                }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) == 112) // ERROR_DISK_FULL
                {
                    _logger.LogMessage($"Operation stopped due to insufficient disk space: {ex.Message}");

                    var tempDrive = new DriveInfo(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\");
                    _messageBoxService.ShowError(
                        $"The operation was stopped because the disk is full.\n\n" +
                        $"Drive {tempDrive.Name} has only {Formatter.FormatBytes(tempDrive.AvailableFreeSpace)} free.\n\n" +
                        $"Please free up space and try again. Large ISO files require temporary space on your system drive.");
                    UpdateStatus("Operation failed: Disk full.");
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
                    LogOperationSummary("Conversion");
                }
            }
            catch (Exception ex)
            {
                _ = ReportBugAsync($"Error during batch conversion process: {ex.Message}", ex);
                _logger.LogMessage($"Error during batch conversion process: {ex.Message}");
                _isOperationRunning = false;
                SetControlsState(true);
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync($"Error during batch conversion process: {ex.Message}", ex);
            _logger.LogMessage($"Error during batch conversion process: {ex.Message}");
            _isOperationRunning = false;
            SetControlsState(true);
        }
    }

    private async void StartTestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isOperationRunning) return;

            _isOperationRunning = true;
            SetControlsState(false); // Disable controls immediately

            await PreOperationCleanupAsync();

            try
            {
                Application.Current.Dispatcher.Invoke(() => LogViewer.Clear());

                // Thread-safe cancellation token replacement
                var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
                oldCts?.Dispose();

                var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");

                if (!File.Exists(extractXisoPath))
                {
                    _logger.LogMessage("Error: extract-xiso.exe not found.");
                    _messageBoxService.ShowError("extract-xiso.exe is missing. Please ensure it's in the application folder.");
                    SetControlsState(true);
                    _isOperationRunning = false;
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
                    SetControlsState(true);
                    _isOperationRunning = false; // Reset flag before early return
                    return;
                }

                if (moveSuccessful && moveFailed && !string.IsNullOrEmpty(successFolder) && successFolder.Equals(failedFolder, StringComparison.OrdinalIgnoreCase))
                {
                    _messageBoxService.ShowError("Success Folder and Failed Folder cannot be the same.");
                    SetControlsState(true);
                    _isOperationRunning = false; // Reset flag before early return
                    return;
                }

                if ((moveSuccessful && !string.IsNullOrEmpty(successFolder) && successFolder.Equals(inputFolder, StringComparison.OrdinalIgnoreCase)) ||
                    (moveFailed && !string.IsNullOrEmpty(failedFolder) && failedFolder.Equals(inputFolder, StringComparison.OrdinalIgnoreCase)))
                {
                    _messageBoxService.ShowError("Success/Failed folder cannot be the same as the Input folder.");
                    SetControlsState(true);
                    _isOperationRunning = false; // Reset flag before early return
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
                    SetControlsState(true);
                    _isOperationRunning = false; // Reset flag before early return
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

                ResetSummaryStats();

                _uiTotalFiles = isoFilesToTest.Count;
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
                _logger.LogMessage($"Total ISOs to test: {isoFilesToTest.Count}.");
                _logger.LogMessage($"Search in subfolders: {searchSubfolders}");
                if (moveSuccessful) _logger.LogMessage($"Moving successful files to: {successFolder}"); // This is a subfolder, so it's fine.
                if (moveFailed) _logger.LogMessage($"Moving failed files to: {failedFolder}");

                try
                {
                    await PerformBatchIsoTestAsync(extractXisoPath, inputFolder, moveSuccessful, successFolder, moveFailed, failedFolder, isoFilesToTest);
                }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) == 112) // ERROR_DISK_FULL
                {
                    _logger.LogMessage($"Operation stopped due to insufficient disk space: {ex.Message}");
                    _messageBoxService.ShowError("The operation was stopped because the disk is full. Please free up some space and try again.");
                    UpdateStatus("Operation failed: Disk full.");
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
                    LogOperationSummary("Test");
                }
            }
            catch (Exception ex)
            {
                _ = ReportBugAsync($"Error during batch ISO test process: {ex.Message}", ex);
                _logger.LogMessage($"Error during batch ISO test process: {ex.Message}");
                _isOperationRunning = false;
                SetControlsState(true);
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync($"Error during batch ISO test process: {ex.Message}", ex);
            _logger.LogMessage($"Error during batch ISO test process: {ex.Message}");
            _isOperationRunning = false;
            SetControlsState(true);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            /* Ignore if already disposed */
        }

        _logger.LogMessage("Cancellation requested. Finishing current file/archive...");
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var aboutWindow = new AboutWindow(_urlOpener, _messageBoxService)
            {
                Owner = this
            };
            aboutWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error opening About window: {ex.Message}");
            _ = ReportBugAsync("Error opening About window", ex);
        }
    }

    private static string? SelectFolder(string description)
    {
        var dialog = new OpenFolderDialog { Title = description };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static bool IsSubdirectory(string parentPath, string childPath)
    {
        var parentUri = new Uri(parentPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? parentPath : parentPath + Path.DirectorySeparatorChar);
        var childUri = new Uri(childPath);

        return parentUri.IsBaseOf(childUri);
    }

    private bool ValidateInputOutputFolders(string inputFolder, string outputFolder)
    {
        if (inputFolder.Equals(outputFolder, StringComparison.OrdinalIgnoreCase))
        {
            _messageBoxService.ShowError("Input and output folders must be different for conversion.");
            return false;
        }

        if (IsSubdirectory(inputFolder, outputFolder) || IsSubdirectory(outputFolder, inputFolder))
            _messageBoxService.ShowWarning("Input and output folders are in the same directory tree. This may cause unexpected behavior.", "Folder Path Warning");
        return true;
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

    private void LogOperationSummary(string operationType)
    {
        _logger.LogMessage("");
        _logger.LogMessage($"--- Batch {operationType.ToLowerInvariant()} completed. ---");
        _logger.LogMessage($"Total files processed: {_uiTotalFiles}");
        _logger.LogMessage($"Successfully {ConvertToPastTense.GetPastTense(operationType)}: {_uiSuccessCount} files");
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
                                    $"Successfully {ConvertToPastTense.GetPastTense(operationType)}: {_uiSuccessCount} files\n" +
                                    $"Skipped: {_uiSkippedCount} files\n" +
                                    $"Failed: {_uiFailedCount} files",
                $"{operationType} Complete", MessageBoxButton.OK,
                _uiFailedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        });
    }

    private async Task TryDeleteFileAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;

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

    private async Task CleanupTempFoldersAsync(List<string> tempFolders)
    {
        if (tempFolders.Count == 0) return;

        _logger.LogMessage("Cleaning up remaining temporary extraction folders...");
        foreach (var folder in tempFolders.ToList())
        {
            var success = await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(folder, 5, 2000, _logger);
            if (success)
            {
                tempFolders.Remove(folder);
            }
        }

        if (tempFolders.Count == 0)
            _logger.LogMessage("Temporary folder cleanup complete.");
        else
            _logger.LogMessage($"WARNING: {tempFolders.Count} temporary folders could not be cleaned automatically.");
    }

    /// <summary>
    /// Attempts to delete a directory with retry logic for locked files
    /// </summary>
    private async Task<bool> TryDeleteDirectoryWithRetryAsync(string directoryPath, int maxRetries, int delayMs)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                    return true;

                await Task.Run(() => Directory.Delete(directoryPath, true), CancellationToken.None);
                return true;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                _logger.LogMessage($"Temp folder deletion attempt {attempt}/{maxRetries} failed (files may be locked). Retrying in {delayMs}ms...");
                await Task.Delay(delayMs, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Error deleting directory {directoryPath}: {ex.Message}");
                return false;
            }
        }

        return false;
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

        // Always reset indeterminate state
        ProgressBar.IsIndeterminate = false;
        if (!enabled) return;

        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
        if (ProgressTextBlock != null)
        {
            ProgressTextBlock.Text = "";
        }
    }

    private void ResetSummaryStats()
    {
        _uiTotalFiles = 0;
        _uiSuccessCount = 0;
        _uiFailedCount = 0;
        _uiSkippedCount = 0;
        _invalidIsoErrorCount = 0; // Reset invalid ISO count
        _totalProcessedFiles = 0; // Reset total processed count
        _failedConversionFilePaths.Clear(); // Clear failed files list
        UpdateSummaryStatsUi();
        UpdateProgressUi(0, 0);
        ProcessingTimeValue.Text = "00:00:00";
        if (!_processingTimer.IsEnabled)
        {
            StopPerformanceCounter();
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        SaveSettings();

        if (_isOperationRunning)
        {
            var result = _messageBoxService.Show("An operation is still running. Do you want to cancel and exit?", "Operation in Progress", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }

            _cts?.Cancel();
        }

        _processingTimer.Tick -= ProcessingTimer_Tick;
        _processingTimer.Stop();
        StopPerformanceCounter();
        // No call to base.OnClosing(e); needed here.
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Checks if a file is currently locked by another process
    /// </summary>
    private static async Task<bool> IsFileLockedAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        return await Task.Run(() =>
        {
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return false; // File is not locked
            }
            catch (IOException)
            {
                return true; // File is locked
            }
            catch
            {
                return false; // Other errors, assume not locked
            }
        });
    }

    private async Task<bool> ValidateDiskSpaceAsync(List<string> topLevelEntries, string outputFolder)
    {
        try
        {
            // Check temp drive space
            var tempPath = Path.GetTempPath();
            var tempRoot = Path.GetPathRoot(tempPath);
            if (string.IsNullOrEmpty(tempRoot))
            {
                _logger.LogMessage("Warning: Could not determine temp drive root.");
                return true;
            }

            var tempDrive = new DriveInfo(tempRoot);

            var outputRoot = Path.GetPathRoot(outputFolder);
            if (string.IsNullOrEmpty(outputRoot))
            {
                _logger.LogMessage("Warning: Could not determine output drive root.");
                return true;
            }

            var outputDrive = new DriveInfo(outputRoot);

            // Calculate approximate space needed (largest file * 3 for safety margin)
            long maxFileSize = 0;
            foreach (var file in topLevelEntries.Where(f => Path.GetExtension(f).Equals(".iso", StringComparison.OrdinalIgnoreCase)))
            {
                var fileInfo = await Task.Run(() => new FileInfo(file));
                if (fileInfo.Length > maxFileSize)
                {
                    maxFileSize = fileInfo.Length;
                }
            }

            // Need space for: temp copy + conversion output
            var requiredTempSpace = maxFileSize * 2; // Safety margin
            var requiredOutputSpace = maxFileSize;

            if (tempDrive.AvailableFreeSpace < requiredTempSpace)
            {
                _messageBoxService.ShowError(
                    $"Insufficient disk space on {tempDrive.Name}\n\n" +
                    $"Available: {Formatter.FormatBytes(tempDrive.AvailableFreeSpace)}\n" +
                    $"Required: ~{Formatter.FormatBytes(requiredTempSpace)}\n\n" +
                    $"Please free up space on your system drive or change your TEMP folder location.");
                return false;
            }

            if (outputDrive.AvailableFreeSpace < requiredOutputSpace)
            {
                _messageBoxService.ShowError(
                    $"Insufficient disk space on output drive {outputDrive.Name}\n\n" +
                    $"Available: {Formatter.FormatBytes(outputDrive.AvailableFreeSpace)}\n" +
                    $"Required: ~{Formatter.FormatBytes(requiredOutputSpace)}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Warning: Could not validate disk space: {ex.Message}");
            return true; // Don't block operation if check fails
        }
    }
}