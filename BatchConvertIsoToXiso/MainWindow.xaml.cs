using System.ComponentModel;
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
    private readonly IIsoOrchestratorService _orchestratorService;
    private readonly IDiskMonitorService _diskMonitorService;

    private CancellationTokenSource _cts = new();
    private readonly IUpdateChecker _updateChecker;
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly IMessageBoxService _messageBoxService;
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

    private int _invalidIsoErrorCount;
    private int _totalProcessedFiles;
    private readonly List<string> _failedConversionFilePaths = [];

    public MainWindow(IUpdateChecker updateChecker, ILogger logger, IBugReportService bugReportService,
        IMessageBoxService messageBoxService, IUrlOpener urlOpener, ISettingsService settingsService,
        IIsoOrchestratorService orchestratorService, IDiskMonitorService diskMonitorService)
    {
        InitializeComponent();

        _updateChecker = updateChecker;
        _logger = logger;
        _bugReportService = bugReportService;
        _messageBoxService = messageBoxService;
        _urlOpener = urlOpener;
        _settingsService = settingsService;
        _orchestratorService = orchestratorService;
        _diskMonitorService = diskMonitorService;

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
            if (_isOperationRunning) return;

            try
            {
                // 1. UI Initialization
                _isOperationRunning = true;
                SetControlsState(false);
                LogViewer.Clear();
                ResetSummaryStats();

                UpdateStatus("Cleaning up temporary files...");
                await PreOperationCleanupAsync();

                // 2. Validation
                var inputFolder = ConversionInputFolderTextBox.Text;
                var outputFolder = ConversionOutputFolderTextBox.Text;

                if (string.IsNullOrEmpty(inputFolder) || string.IsNullOrEmpty(outputFolder))
                {
                    _messageBoxService.ShowError("Please select both input and output folders for conversion.");
                    FinalizeUiState();
                    return;
                }

                if (!ValidateInputOutputFolders(inputFolder, outputFolder))
                {
                    FinalizeUiState();
                    return;
                }

                // 3. Prepare Cancellation
                var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
                oldCts.Dispose();

                // 4. Define Progress Reporting Logic
                // IProgress<T> automatically dispatches to the UI thread
                var progress = new Progress<BatchOperationProgress>(p =>
                {
                    if (p.LogMessage != null) _logger.LogMessage(p.LogMessage);
                    if (p.StatusText != null) UpdateStatus(p.StatusText);

                    if (p.TotalFiles.HasValue)
                    {
                        _uiTotalFiles = p.TotalFiles.Value;
                        UpdateSummaryStatsUi();
                    }

                    if (p.ProcessedCount.HasValue)
                    {
                        UpdateProgressUi(p.ProcessedCount.Value, _uiTotalFiles);
                    }

                    if (p.SuccessCount.HasValue)
                    {
                        _uiSuccessCount += p.SuccessCount.Value;
                        UpdateSummaryStatsUi();
                    }

                    if (p.FailedCount.HasValue)
                    {
                        _uiFailedCount += p.FailedCount.Value;
                        UpdateSummaryStatsUi();
                    }

                    if (p.SkippedCount.HasValue)
                    {
                        _uiSkippedCount += p.SkippedCount.Value;
                        UpdateSummaryStatsUi();
                    }

                    if (p.CurrentDrive != null) SetCurrentOperationDrive(p.CurrentDrive);
                    if (p.FailedPathToAdd != null) _failedConversionFilePaths.Add(p.FailedPathToAdd);

                    ProgressBar.IsIndeterminate = p.IsIndeterminate;
                });

                // 5. Start Operation
                _operationStartTime = DateTime.Now;
                _processingTimer.Start();
                UpdateStatus("Starting batch conversion...");

                try
                {
                    await _orchestratorService.ConvertAsync(
                        inputFolder,
                        outputFolder,
                        DeleteOriginalsCheckBox.IsChecked ?? false,
                        SkipSystemUpdateCheckBox.IsChecked ?? false,
                        SearchSubfoldersConversionCheckBox.IsChecked ?? false,
                        progress,
                        HandleCloudRetryRequest, // Callback for OneDrive/Cloud files
                        _cts.Token);
                }
                catch (ExceptionFormatter.CriticalToolFailureException ex)
                {
                    UpdateStatus("Operation stopped: Tool inaccessible.");
                    ShowCriticalToolFailureMessage(ex.Message);
                }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) == 112) // Disk Full
                {
                    _logger.LogMessage($"Operation stopped: Disk Full. {ex.Message}");
                    _messageBoxService.ShowError("The operation was stopped because the disk is full.");
                    UpdateStatus("Operation failed: Disk full.");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogMessage("Operation was canceled by the user.");
                    UpdateStatus("Operation canceled.");
                }
                catch (Exception ex)
                {
                    _logger.LogMessage($"Critical Error: {ex.Message}");
                    _ = ReportBugAsync("Critical error during conversion", ex);
                }
                finally
                {
                    FinalizeUiState();
                    LogOperationSummary("Conversion");
                }
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Unexpected UI Error: {ex.Message}");
                FinalizeUiState();
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("Unexpected UI Error", ex);
        }
    }

    /// <summary>
    /// Helper to reset UI state after operation finishes or fails
    /// </summary>
    private void FinalizeUiState()
    {
        _processingTimer.Stop();
        _diskMonitorService.StopMonitoring();
        StopPerformanceCounter();
        _isOperationRunning = false;
        SetControlsState(true);

        var finalElapsedTime = DateTime.Now - _operationStartTime;
        ProcessingTimeValue.Text = finalElapsedTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Callback passed to the Service to handle Cloud/OneDrive file prompts on the UI thread
    /// </summary>
    private async Task<CloudRetryResult> HandleCloudRetryRequest(string fileName)
    {
        // Since this is called from the service, we must ensure the MessageBox shows on the UI thread
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var result = _messageBoxService.Show(
                $"The file '{fileName}' is stored in the cloud and needs to be downloaded.\n\n" +
                "• Click 'Yes' to Retry.\n" +
                "• Click 'No' to Skip.\n" +
                "• Click 'Cancel' to stop the batch.",
                "Cloud File Required",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Information);

            return result switch
            {
                MessageBoxResult.Yes => CloudRetryResult.Retry,
                MessageBoxResult.No => CloudRetryResult.Skip,
                _ => CloudRetryResult.Cancel
            };
        });
    }


    private async void StartTestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isOperationRunning) return;

            // 1. UI Initialization
            _isOperationRunning = true;
            SetControlsState(false);
            LogViewer.Clear();
            ResetSummaryStats();

            UpdateStatus("Cleaning up temporary files...");
            await PreOperationCleanupAsync();

            // 2. Validation
            var inputFolder = TestInputFolderTextBox.Text;
            if (string.IsNullOrEmpty(inputFolder))
            {
                _messageBoxService.ShowError("Please select the input folder for testing.");
                FinalizeUiState();
                return;
            }

            var moveSuccessful = MoveSuccessFilesCheckBox.IsChecked == true;
            var moveFailed = MoveFailedFilesCheckBox.IsChecked == true;
            var successFolder = Path.Combine(inputFolder, "_success");
            var failedFolder = Path.Combine(inputFolder, "_failed");

            if (moveSuccessful && moveFailed && successFolder.Equals(failedFolder, StringComparison.OrdinalIgnoreCase))
            {
                _messageBoxService.ShowError("Success Folder and Failed Folder cannot be the same.");
                FinalizeUiState();
                return;
            }

            // 3. Prepare Cancellation
            var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            oldCts.Dispose();

            // 4. Define Progress Reporting Logic
            var progress = new Progress<BatchOperationProgress>(p =>
            {
                if (p.LogMessage != null) _logger.LogMessage(p.LogMessage);
                if (p.StatusText != null) UpdateStatus(p.StatusText);

                if (p.TotalFiles.HasValue)
                {
                    _uiTotalFiles = p.TotalFiles.Value;
                    _logger.LogMessage($"Found {_uiTotalFiles} .iso files for testing.");
                    UpdateSummaryStatsUi();
                }

                if (p.ProcessedCount.HasValue)
                {
                    UpdateProgressUi(p.ProcessedCount.Value, _uiTotalFiles);
                }

                if (p.SuccessCount.HasValue)
                {
                    _uiSuccessCount += p.SuccessCount.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.FailedCount.HasValue)
                {
                    _uiFailedCount += p.FailedCount.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.CurrentDrive != null) SetCurrentOperationDrive(p.CurrentDrive);
                if (p.FailedPathToAdd != null) _failedConversionFilePaths.Add(p.FailedPathToAdd);

                ProgressBar.IsIndeterminate = p.IsIndeterminate;
            });

            // 5. Start Operation
            _operationStartTime = DateTime.Now;
            _processingTimer.Start();
            UpdateStatus("Starting batch ISO test...");
            _logger.LogMessage("--- Starting batch ISO test process... ---");

            try
            {
                await _orchestratorService.TestAsync(
                    inputFolder,
                    moveSuccessful,
                    moveFailed,
                    SearchSubfoldersTestCheckBox.IsChecked ?? false,
                    progress,
                    HandleCloudRetryRequest,
                    _cts.Token);
            }
            catch (ExceptionFormatter.CriticalToolFailureException ex)
            {
                UpdateStatus("Operation stopped: Tool inaccessible.");
                ShowCriticalToolFailureMessage(ex.Message);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 112) // Disk Full
            {
                _logger.LogMessage($"Operation stopped: Disk Full. {ex.Message}");
                _messageBoxService.ShowError("The operation was stopped because the disk is full.");
                UpdateStatus("Operation failed: Disk full.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogMessage("Operation was canceled by the user.");
                UpdateStatus("Operation canceled.");
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Critical Error: {ex.Message}");
                _ = ReportBugAsync("Critical error during ISO test", ex);
            }
            finally
            {
                FinalizeUiState();
                UpdateStatus("Test complete. Ready.");
                LogOperationSummary("Test");
            }
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("Unexpected UI Error in StartTestButton", ex);
            FinalizeUiState();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _cts.Cancel();
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

        try
        {
            var pathRoot = Path.GetPathRoot(outputFolder);
            if (!string.IsNullOrEmpty(pathRoot) &&
                outputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                _messageBoxService.ShowError("Selecting the root of a drive (e.g., C:\\) as an output folder is not allowed due to Windows permission restrictions. Please select or create a subfolder.");
                return false;
            }
        }
        catch
        {
            /* Ignore parsing errors here */
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
                var percentage = (double)current / total * 100;
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

        if (_uiFailedCount > 0)
        {
            _logger.LogMessage($"Failed to {operationType.ToLowerInvariant()}: {_uiFailedCount} files");

            _logger.LogMessage($"\nList of files that failed the {operationType.ToLowerInvariant()} (original names):");
            foreach (var originalPath in _failedConversionFilePaths)
            {
                _logger.LogMessage($"- {Path.GetFileName(originalPath)}");
            }

            _logger.LogMessage("");
        }

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // ... existing warning logic for invalid ISOs ...
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

    private void ProcessingTimer_Tick(object? sender, EventArgs e)
    {
        var elapsedTime = DateTime.Now - _operationStartTime;
        ProcessingTimeValue.Text = elapsedTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        // Replace old logic with service call
        WriteSpeedValue.Text = _diskMonitorService.GetCurrentWriteSpeedFormatted();
        WriteSpeedDriveIndicator.Text = _diskMonitorService.CurrentDriveLetter != null
            ? $"({_diskMonitorService.CurrentDriveLetter})"
            : "";
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
        ProgressTextBlock?.Text = "";
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

            _cts.Cancel();
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

    private void ShowCriticalToolFailureMessage(string detail)
    {
        var message = $"{detail}\n\n" +
                      "Possible Solutions:\n" +
                      "1. Antivirus: Check if your Antivirus (e.g., Windows Defender) quarantined 'extract-xiso.exe'. Add the application folder to your Antivirus Exclusion/Exemption list.\n" +
                      "2. Permissions: Try running this application as Administrator.\n" +
                      "3. Drive Issues: If the files are on an external drive, ensure it hasn't been disconnected.\n\n" +
                      "The batch operation has been stopped to prevent further errors.";

        _messageBoxService.ShowError(message);
    }
}