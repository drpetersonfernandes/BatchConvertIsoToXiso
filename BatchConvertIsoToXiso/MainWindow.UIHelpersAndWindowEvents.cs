using System.Globalization;
using System.IO;
using System.Windows;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;
using Microsoft.Win32;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
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

    private async Task PreOperationCleanupAsync()
    {
        _logger.LogMessage("Performing pre-operation cleanup of temporary folders...");
        await TempFolderCleanupHelper.CleanupBatchConvertTempFoldersAsync(_logger);
        _logger.LogMessage("Pre-operation cleanup completed.");
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
        var normalizedInput = Path.GetFullPath(inputFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedOutput = Path.GetFullPath(outputFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (normalizedInput.Equals(normalizedOutput, StringComparison.OrdinalIgnoreCase))
        {
            _messageBoxService.ShowError("Input and output folders must be different.");
            return false;
        }

        // Check if output folder is a subfolder of input folder
        if (normalizedOutput.StartsWith(normalizedInput + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _messageBoxService.ShowError("Output folder cannot be a subfolder of the input folder. This would cause recursive processing issues.");
            return false;
        }

        return true;
    }

    private void UpdateSummaryStatsUi()
    {
        TotalFilesValue.Text = _uiTotalFiles.ToString(CultureInfo.InvariantCulture);
        SuccessValue.Text = _uiSuccessCount.ToString(CultureInfo.InvariantCulture);
        FailedValue.Text = _uiFailedCount.ToString(CultureInfo.InvariantCulture);
        SkippedValue.Text = _uiSkippedCount.ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateProgressUi(int current, int total)
    {
        // Don't update determinate text if we haven't received a total yet
        if (total <= 0) return;

        ProgressBar.Maximum = total;
        ProgressBar.Value = current;

        if (ProgressBar.Visibility == Visibility.Visible && !ProgressBar.IsIndeterminate)
        {
            var percentage = (double)current / total * 100;
            ProgressTextBlock.Text = $"{current} of {total} ({percentage:F0}%)";
        }
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

        // Reset Progress Bar to a clean state
        ProgressBar.Value = 0;
        ProgressBar.Maximum = 1;
        ProgressBar.IsIndeterminate = false;
        ProgressTextBlock.Text = "";
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

    private void StopPerformanceCounter()
    {
        _diskMonitorService.StopMonitoring();
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            WriteSpeedValue?.Text = "N/A";
            WriteSpeedDriveIndicator?.Text = "";
        });
    }

    private void UpdateStatus(string status)
    {
        StatusTextBlock.Text = status;
    }

    private void SetCurrentOperationDrive(string? driveLetter)
    {
        _diskMonitorService.StartMonitoring(driveLetter);
    }
}