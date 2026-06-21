using System.IO;
using System.Windows;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
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

    private async void StartConversionButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isOperationRunning) return;

            _isOperationRunning = true;
            _operationCompletedTcs = new TaskCompletionSource();
            SetControlsState(false);
            LogViewer.Clear();
            ResetSummaryStats();

            // Immediate visual feedback while the background thread scans the filesystem
            ProgressBar.IsIndeterminate = true;
            ProgressTextBlock.Text = "Scanning folders for files...";

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

            if (!Directory.Exists(inputFolder))
            {
                _messageBoxService.ShowError($"The input folder no longer exists:\n{inputFolder}");
                FinalizeUiState();
                return;
            }

            if (!Directory.Exists(outputFolder))
            {
                _messageBoxService.ShowError($"The output folder no longer exists:\n{outputFolder}");
                FinalizeUiState();
                return;
            }

            if (!ValidateInputOutputFolders(inputFolder, outputFolder))
            {
                FinalizeUiState();
                return;
            }

            var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            try { oldCts.Dispose(); } catch { /* Already disposed by CleanupResources */ }

            var progress = new Progress<BatchOperationProgress>(p =>
            {
                try
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

                    if (p.SkippedCount.HasValue)
                    {
                        _uiSkippedCount += p.SkippedCount.Value;
                        _totalProcessedFiles += p.SkippedCount.Value;
                        UpdateSummaryStatsUi();
                    }

                    if (p.CurrentDrive != null) SetCurrentOperationDrive(p.CurrentDrive);
                    if (p.FailedPathToAdd != null) _failedFilePaths.Add(p.FailedPathToAdd);

                    if (p.TotalFiles.HasValue || p.ProcessedCount.HasValue)
                    {
                        ProgressBar.IsIndeterminate = false;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation exceptions during UI updates
                }
            });

            _operationStopwatch.Restart();
            _processingTimer.Start();
            _memoryTimer.Start();
            UpdateStatus("Starting batch conversion...");

            // Determine conversion method from radio buttons
            // If neither extract-xiso nor xdvdfs is selected, the built-in writer is used
            var useExtractXiso = UseExtractXisoRadioButton.IsChecked == true;
            var useXdvdfs = UseXdvdfsRadioButton.IsChecked == true;

            await _orchestratorService.ConvertAsync(
                inputFolder, outputFolder,
                DeleteOriginalsCheckBox.IsChecked ?? false,
                SkipSystemUpdateCheckBox.IsChecked ?? false,
                CheckOutputIntegrityCheckBox.IsChecked ?? false,
                SearchSubfoldersConversionCheckBox.IsChecked ?? false,
                useExtractXiso,
                useXdvdfs,
                progress, HandleCloudRetryRequestAsync, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Operation canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Critical Error: {ex.Message}");
            _ = _bugReportService.SendBugReportAsync("Critical error during batch conversion", ex);
        }
        finally
        {
            FinalizeUiState();
            await LogOperationSummaryAsync("Conversion");
        }
    }

    private async void StartTestButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isOperationRunning) return;

            _isOperationRunning = true;
            _operationCompletedTcs = new TaskCompletionSource();
            SetControlsState(false);
            LogViewer.Clear();
            ResetSummaryStats();

            // Immediate visual feedback while the background thread scans the filesystem
            ProgressBar.IsIndeterminate = true;
            ProgressTextBlock.Text = "Scanning folders for ISOs...";

            UpdateStatus("Cleaning up temporary files...");
            await PreOperationCleanupAsync();

            var inputFolder = TestInputFolderTextBox.Text;
            if (string.IsNullOrEmpty(inputFolder))
            {
                _messageBoxService.ShowError("Please select the input folder for testing.");
                FinalizeUiState();
                return;
            }

            if (!Directory.Exists(inputFolder))
            {
                _messageBoxService.ShowError($"The input folder no longer exists:\n{inputFolder}");
                FinalizeUiState();
                return;
            }

            var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            try { oldCts.Dispose(); } catch { /* Already disposed by CleanupResources */ }

            var progress = new Progress<BatchOperationProgress>(p =>
            {
                try
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

                    if (p.TotalFiles.HasValue || p.ProcessedCount.HasValue)
                    {
                        ProgressBar.IsIndeterminate = false;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation exceptions during UI updates
                }
            });

            _operationStopwatch.Restart();
            _processingTimer.Start();
            _memoryTimer.Start();
            UpdateStatus("Starting batch ISO test...");

            await _orchestratorService.TestAsync(
                inputFolder,
                MoveSuccessFilesCheckBox.IsChecked == true,
                MoveFailedFilesCheckBox.IsChecked == true,
                SearchSubfoldersTestCheckBox.IsChecked == true,
                PerformDeepScanCheckBox.IsChecked ?? false,
                progress, HandleCloudRetryRequestAsync, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Operation canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Critical Error: {ex.Message}");
            _ = _bugReportService.SendBugReportAsync("Critical error during batch test", ex);
        }
        finally
        {
            FinalizeUiState();
            await LogOperationSummaryAsync("Test");
        }
    }
}
