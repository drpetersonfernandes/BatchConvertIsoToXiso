using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Threading;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;
using BatchConvertIsoToXiso.Services.Xiso;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private readonly IIsoOrchestratorService _orchestratorService;
    private readonly IDiskMonitorService _diskMonitorService;
    private readonly INativeIsoIntegrityService _nativeIsoTester;

    private CancellationTokenSource _cts = new();
    private readonly IUpdateChecker _updateChecker;
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly IUrlOpener _urlOpener;

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
    private readonly List<string> _failedFilePaths = [];

    // XIso Explorer State
    private IsoSt? _explorerIsoSt;
    private readonly Stack<FileEntry> _explorerHistory = new();
    private readonly Stack<string> _explorerPathNames = new();

    public MainWindow(IUpdateChecker updateChecker, ILogger logger, IBugReportService bugReportService,
        IMessageBoxService messageBoxService, IUrlOpener urlOpener,
        IIsoOrchestratorService orchestratorService, IDiskMonitorService diskMonitorService, INativeIsoIntegrityService nativeIsoTester)
    {
        InitializeComponent();

        _updateChecker = updateChecker;
        _logger = logger;
        _bugReportService = bugReportService;
        _messageBoxService = messageBoxService;
        _urlOpener = urlOpener;
        _orchestratorService = orchestratorService;
        _diskMonitorService = diskMonitorService;
        _nativeIsoTester = nativeIsoTester;

        _logger.Initialize(LogViewer);

        _processingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _processingTimer.Tick += ProcessingTimer_Tick;

        ResetSummaryStats();
        DisplayInitialInstructions();
        _ = CheckForUpdatesAsync();
    }

    #region Conversion & Testing Logic

    private void UpdateStatus(string status)
    {
        StatusTextBlock.Text = status;
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
            return;
        }

        ConversionInputFolderTextBox.Text = inputFolder;
    }

    private void BrowseConversionOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var outputFolder = SelectFolder("Select the output folder for converted XISO files");
        if (string.IsNullOrEmpty(outputFolder)) return;

        if (CheckForTempPath.IsSystemTempPath(outputFolder))
        {
            _messageBoxService.ShowError("The system's temporary folder or a subfolder within it cannot be selected as an output folder. Please choose a different location.");
            return;
        }

        ConversionOutputFolderTextBox.Text = outputFolder;
    }

    private void BrowseTestInputButton_Click(object sender, RoutedEventArgs e)
    {
        var inputFolder = SelectFolder("Select the folder containing ISO files to test");
        if (string.IsNullOrEmpty(inputFolder)) return;

        if (CheckForTempPath.IsSystemTempPath(inputFolder))
        {
            _messageBoxService.ShowError("The system's temporary folder or a subfolder within it cannot be selected as an input folder for testing. Please choose a different location.");
            return;
        }

        TestInputFolderTextBox.Text = inputFolder;
    }

    private async void StartConversionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isOperationRunning) return;

            _isOperationRunning = true;
            SetControlsState(false);
            LogViewer.Clear();
            ResetSummaryStats();

            UpdateStatus("Cleaning up temporary files...");
            await PreOperationCleanupAsync();

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

            var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            oldCts.Dispose();

            var progress = new Progress<BatchOperationProgress>(p =>
            {
                if (p.LogMessage != null) _logger.LogMessage(p.LogMessage);
                if (p.StatusText != null) UpdateStatus(p.StatusText);
                if (p.TotalFiles.HasValue)
                {
                    _uiTotalFiles = p.TotalFiles.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.ProcessedCount.HasValue) UpdateProgressUi(p.ProcessedCount.Value, _uiTotalFiles);

                if (p.SuccessCount.HasValue)
                {
                    _uiSuccessCount += p.SuccessCount.Value;
                    _totalProcessedFiles += p.SuccessCount.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.FailedCount.HasValue)
                {
                    _uiFailedCount += p.FailedCount.Value;
                    _totalProcessedFiles += p.FailedCount.Value;
                    _invalidIsoErrorCount += p.FailedCount.Value; // Assume failure is often due to invalid ISO
                    UpdateSummaryStatsUi();
                }

                if (p.SkippedCount.HasValue)
                {
                    _uiSkippedCount += p.SkippedCount.Value;
                    _totalProcessedFiles += p.SkippedCount.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.CurrentDrive != null) SetCurrentOperationDrive(p.CurrentDrive);
                if (p.FailedPathToAdd != null) _failedFilePaths.Add(p.FailedPathToAdd);
                ProgressBar.IsIndeterminate = p.IsIndeterminate;
            });

            _operationStartTime = DateTime.Now;
            _processingTimer.Start();
            UpdateStatus("Starting batch conversion...");

            await _orchestratorService.ConvertAsync(
                inputFolder, outputFolder,
                DeleteOriginalsCheckBox.IsChecked ?? false,
                SkipSystemUpdateCheckBox.IsChecked ?? false,
                CheckOutputIntegrityCheckBox.IsChecked ?? false,
                SearchSubfoldersConversionCheckBox.IsChecked ?? false,
                progress, HandleCloudRetryRequest, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Operation canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Critical Error: {ex.Message}");
        }
        finally
        {
            FinalizeUiState();
            LogOperationSummary("Conversion");
        }
    }

    private async void StartTestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isOperationRunning) return;

            _isOperationRunning = true;
            SetControlsState(false);
            LogViewer.Clear();
            ResetSummaryStats();

            UpdateStatus("Cleaning up temporary files...");
            await PreOperationCleanupAsync();

            var inputFolder = TestInputFolderTextBox.Text;
            if (string.IsNullOrEmpty(inputFolder))
            {
                _messageBoxService.ShowError("Please select the input folder for testing.");
                FinalizeUiState();
                return;
            }

            var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            oldCts.Dispose();

            var progress = new Progress<BatchOperationProgress>(p =>
            {
                if (p.LogMessage != null) _logger.LogMessage(p.LogMessage);
                if (p.StatusText != null) UpdateStatus(p.StatusText);
                if (p.TotalFiles.HasValue)
                {
                    _uiTotalFiles = p.TotalFiles.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.ProcessedCount.HasValue) UpdateProgressUi(p.ProcessedCount.Value, _uiTotalFiles);

                if (p.SuccessCount.HasValue)
                {
                    _uiSuccessCount += p.SuccessCount.Value;
                    _totalProcessedFiles += p.SuccessCount.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.FailedCount.HasValue)
                {
                    _uiFailedCount += p.FailedCount.Value;
                    _totalProcessedFiles += p.FailedCount.Value;
                    _invalidIsoErrorCount += p.FailedCount.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.CurrentDrive != null) SetCurrentOperationDrive(p.CurrentDrive);
                if (p.FailedPathToAdd != null) _failedFilePaths.Add(p.FailedPathToAdd);
                ProgressBar.IsIndeterminate = p.IsIndeterminate;
            });

            _operationStartTime = DateTime.Now;
            _processingTimer.Start();
            UpdateStatus("Starting batch ISO test...");

            await _orchestratorService.TestAsync(
                inputFolder,
                MoveSuccessFilesCheckBox.IsChecked == true,
                MoveFailedFilesCheckBox.IsChecked == true,
                SearchSubfoldersTestCheckBox.IsChecked ?? false,
                progress, HandleCloudRetryRequest, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Operation canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Critical Error: {ex.Message}");
        }
        finally
        {
            FinalizeUiState();
            LogOperationSummary("Test");
        }
    }

    #endregion

    #region XIso Explorer Logic

    private void BrowseExplorerFile_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = "Xbox ISO files (*.iso)|*.iso|All files (*.*)|*.*",
            Title = "Select an Xbox ISO to explore"
        };

        if (openFileDialog.ShowDialog() != true) return;

        ExplorerFilePathTextBox.Text = openFileDialog.FileName;
        InitializeExplorer(openFileDialog.FileName);
    }

    private void InitializeExplorer(string isoPath)
    {
        try
        {
            _explorerIsoSt?.Dispose();
            _explorerHistory.Clear();
            _explorerPathNames.Clear();

            _explorerIsoSt = new IsoSt(isoPath);
            var volume = VolumeDescriptor.ReadFrom(_explorerIsoSt);
            var root = FileEntry.CreateRootEntry(volume.RootDirTableSector);

            LoadDirectory(root, "Root");
        }
        catch (Exception ex)
        {
            _messageBoxService.ShowError($"Failed to read XISO: {ex.Message}");
        }
    }

    private void LoadDirectory(FileEntry dirEntry, string folderName)
    {
        if (_explorerIsoSt == null) return;

        try
        {
            var entries = _nativeIsoTester.GetDirectoryEntries(_explorerIsoSt, dirEntry);
            var uiItems = entries.Select(static e => new XisoExplorerItem
            {
                Name = e.FileName,
                IsDirectory = e.IsDirectory,
                SizeFormatted = e.IsDirectory ? "" : Formatter.FormatBytes(e.FileSize),
                Entry = e
            }).OrderByDescending(static i => i.IsDirectory).ThenBy(static i => i.Name).ToList();

            ExplorerListView.ItemsSource = uiItems;

            if (folderName != "Root")
            {
                _explorerHistory.Push(dirEntry);
                _explorerPathNames.Push(folderName);
            }
            else
            {
                _explorerHistory.Clear();
                _explorerPathNames.Clear();
            }

            UpdateExplorerUiState();
        }
        catch (Exception ex)
        {
            _messageBoxService.ShowError($"Error loading directory: {ex.Message}");
        }
    }

    private void UpdateExplorerUiState()
    {
        ExplorerUpButton.IsEnabled = _explorerHistory.Count > 0;
        var path = "/" + string.Join("/", _explorerPathNames.Reverse());
        ExplorerPathTextBlock.Text = path;
    }

    private void ExplorerListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ExplorerListView.SelectedItem is XisoExplorerItem { IsDirectory: true } item)
        {
            LoadDirectory(item.Entry, item.Name);
        }
    }

    private void ExplorerUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_explorerIsoSt == null) return;

        if (_explorerHistory.Count <= 1)
        {
            var volume = VolumeDescriptor.ReadFrom(_explorerIsoSt);
            LoadDirectory(FileEntry.CreateRootEntry(volume.RootDirTableSector), "Root");
        }
        else
        {
            _explorerHistory.Pop();
            _explorerPathNames.Pop();

            var parentEntry = _explorerHistory.Pop();
            var parentName = _explorerPathNames.Pop();

            LoadDirectory(parentEntry, parentName);
        }
    }

    #endregion

    #region UI Helpers & Window Events

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

    private async Task<CloudRetryResult> HandleCloudRetryRequest(string fileName)
    {
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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        _logger.LogMessage("Cancellation requested. Finishing current file...");
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var aboutWindow = new AboutWindow(_urlOpener, _messageBoxService) { Owner = this };
        aboutWindow.ShowDialog();
    }

    private static string? SelectFolder(string description)
    {
        var dialog = new OpenFolderDialog { Title = description };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private bool ValidateInputOutputFolders(string inputFolder, string outputFolder)
    {
        if (inputFolder.Equals(outputFolder, StringComparison.OrdinalIgnoreCase))
        {
            _messageBoxService.ShowError("Input and output folders must be different.");
            return false;
        }

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
            ProgressBar.Maximum = total > 0 ? total : 1;
            ProgressBar.Value = current;
            if (total > 0 && ProgressBar.Visibility == Visibility.Visible)
            {
                var percentage = (double)current / total * 100;
                ProgressTextBlock.Text = $"{current} of {total} ({percentage:F0}%)";
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
            _logger.LogMessage("\nList of files that failed (original names):");
            foreach (var originalPath in _failedFilePaths)
            {
                _logger.LogMessage($"- {Path.GetFileName(originalPath)}");
            }

            _logger.LogMessage("");
        }

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_totalProcessedFiles > 5 && (double)_invalidIsoErrorCount / _totalProcessedFiles > 0.5)
            {
                _messageBoxService.ShowWarning($"Many files ({_invalidIsoErrorCount} out of {_totalProcessedFiles}) were not valid Xbox ISOs. " +
                                               "Please ensure you are selecting the correct ISO files from Xbox or Xbox 360 games.", "High Rate of Invalid ISOs Detected");
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
        WriteSpeedValue.Text = _diskMonitorService.GetCurrentWriteSpeedFormatted();
        WriteSpeedDriveIndicator.Text = _diskMonitorService.CurrentDriveLetter != null ? $"({_diskMonitorService.CurrentDriveLetter})" : "";
    }

    private void SetControlsState(bool enabled)
    {
        // Disable/Enable the navigation buttons in the header
        BtnNavConvert.IsEnabled = enabled;
        BtnNavTest.IsEnabled = enabled;
        BtnNavExplorer.IsEnabled = enabled;

        // Disable/Enable the entire settings area
        ControlsBorder.IsEnabled = enabled;

        // Toggle visibility of progress and cancel
        ProgressAreaGrid.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProgressBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

        if (enabled)
        {
            UpdateStatus("Ready.");
        }
    }

    private void ResetSummaryStats()
    {
        _uiTotalFiles = _uiSuccessCount = _uiFailedCount = _uiSkippedCount = 0;
        _invalidIsoErrorCount = 0;
        _totalProcessedFiles = 0;
        _failedFilePaths.Clear();
        UpdateSummaryStatsUi();
        UpdateProgressUi(0, 0);
    }

    private void NavConvert_Click(object sender, RoutedEventArgs e)
    {
        ConvertView.Visibility = Visibility.Visible;
        TestView.Visibility = Visibility.Collapsed;
        ExplorerHeaderView.Visibility = Visibility.Collapsed;

        LogBorder.Visibility = Visibility.Visible;
        ExplorerBorder.Visibility = Visibility.Collapsed;
        StatsPanel.Visibility = Visibility.Visible;
    }

    private void NavTest_Click(object sender, RoutedEventArgs e)
    {
        ConvertView.Visibility = Visibility.Collapsed;
        TestView.Visibility = Visibility.Visible;
        ExplorerHeaderView.Visibility = Visibility.Collapsed;

        LogBorder.Visibility = Visibility.Visible;
        ExplorerBorder.Visibility = Visibility.Collapsed;
        StatsPanel.Visibility = Visibility.Visible;
    }

    private void NavExplorer_Click(object sender, RoutedEventArgs e)
    {
        ConvertView.Visibility = Visibility.Collapsed;
        TestView.Visibility = Visibility.Collapsed;
        ExplorerHeaderView.Visibility = Visibility.Visible;

        LogBorder.Visibility = Visibility.Collapsed;
        ExplorerBorder.Visibility = Visibility.Visible;
        StatsPanel.Visibility = Visibility.Collapsed;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_isOperationRunning)
        {
            var result = _messageBoxService.Show("An operation is still running. Exit anyway?", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }

            _cts.Cancel();
        }

        _explorerIsoSt?.Dispose();
        _processingTimer.Stop();
        StopPerformanceCounter();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    #endregion
}