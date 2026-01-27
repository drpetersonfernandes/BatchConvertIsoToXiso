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

    private async void StartConversionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isOperationRunning) return;

            _isOperationRunning = true;
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
                    // Switch from "Scanning" (Indeterminate) to "Processing" (Determinate)
                    ProgressBar.IsIndeterminate = false;
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

                if (p.SkippedCount.HasValue)
                {
                    _uiSkippedCount += p.SkippedCount.Value;
                    _totalProcessedFiles += p.SkippedCount.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.CurrentDrive != null) SetCurrentOperationDrive(p.CurrentDrive);
                if (p.FailedPathToAdd != null) _failedFilePaths.Add(p.FailedPathToAdd);

                // Allow the orchestrator to toggle indeterminate mode (e.g., during a long extraction)
                if (p.IsIndeterminate)
                {
                    ProgressBar.IsIndeterminate = true;
                }
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

            var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            oldCts.Dispose();

            var progress = new Progress<BatchOperationProgress>(p =>
            {
                if (p.LogMessage != null) _logger.LogMessage(p.LogMessage);
                if (p.StatusText != null) UpdateStatus(p.StatusText);

                if (p.TotalFiles.HasValue)
                {
                    _uiTotalFiles = p.TotalFiles.Value;
                    // Switch from "Scanning" (Indeterminate) to "Processing" (Determinate)
                    ProgressBar.IsIndeterminate = false;
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

                // Allow the orchestrator to toggle indeterminate mode
                if (p.IsIndeterminate)
                {
                    ProgressBar.IsIndeterminate = true;
                }
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
}