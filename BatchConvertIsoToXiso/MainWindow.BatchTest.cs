using System.IO;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private async Task PerformBatchIsoTestAsync(bool moveSuccessful, string? successFolder, bool moveFailed, string? failedFolder, List<string> isoFilesToTest)
    {
        var topLevelItemsProcessed = 0;

        _logger.LogMessage($"Found {isoFilesToTest.Count} .iso files for testing.");
        UpdateProgressUi(0, _uiTotalFiles);
        _logger.LogMessage($"Starting test... Total .iso files to test: {_uiTotalFiles}.");

        var testFileIndex = 1; // Counter for simple test filenames

        foreach (var isoFilePath in isoFilesToTest)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var isoFileName = Path.GetFileName(isoFilePath);

            UpdateStatus($"Testing: {isoFileName}");
            _logger.LogMessage($"Testing ISO: {isoFileName}...");

            // Drive for testing (temp) vs. moving successful (output)
            SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath())); // Test extraction always uses the temp path

            var testStatus = await TestSingleIsoAsync(isoFilePath, testFileIndex);
            testFileIndex++;

            if (testStatus == IsoTestResultStatus.Passed)
            {
                _uiSuccessCount++;
                UpdateSummaryStatsUi(); // Update UI after incrementing success count
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
                UpdateSummaryStatsUi(); // Update UI after incrementing failed count
                _failedConversionFilePaths.Add(isoFilePath); // Use the general failed paths list
                _logger.LogMessage($"  FAILURE: '{isoFileName}' failed test.");

                if (moveFailed && !string.IsNullOrEmpty(failedFolder))
                {
                    SetCurrentOperationDrive(GetDriveLetter(failedFolder)); // Switch drive for move operation
                    await _fileMover.MoveTestedFileAsync(isoFilePath, failedFolder, "failed test", _cts.Token);
                }
            }

            topLevelItemsProcessed++; // Increment after processing each top-level ISO
            UpdateProgressUi(topLevelItemsProcessed, _uiTotalFiles);
        }

        if (!_cts.Token.IsCancellationRequested)
        {
            UpdateProgressUi(topLevelItemsProcessed, _uiTotalFiles);
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

    private async Task<IsoTestResultStatus> TestSingleIsoAsync(string isoFilePath, int fileIndex)
    {
        var isoFileName = Path.GetFileName(isoFilePath);
        var tempExtractionDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_TestExtract", Guid.NewGuid().ToString());

        try
        {
            await Task.Run(() => Directory.CreateDirectory(tempExtractionDir), _cts.Token);
            _logger.LogMessage($"  Comprehensive Test for '{isoFileName}'");

            if (!File.Exists(isoFilePath))
            {
                _logger.LogMessage($"  ERROR: ISO file does not exist: {isoFilePath}");
                return IsoTestResultStatus.Failed;
            }

            // Basic file validation
            try
            {
                var fileInfo = new FileInfo(isoFilePath);
                if (fileInfo.Length == 0)
                {
                    _logger.LogMessage($"  ERROR: ISO file is empty: {isoFileName}");
                    return IsoTestResultStatus.Failed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"  ERROR: Cannot open or check ISO file: {ex.Message}");
                return IsoTestResultStatus.Failed;
            }

            // ---------------------------------------------------------
            // ATTEMPT 1: Direct Extraction via Service
            // ---------------------------------------------------------
            _logger.LogMessage($"  Attempting direct extraction test on '{isoFileName}'...");
            var directSuccess = await _externalToolService.RunIsoExtractionAsync(isoFilePath, tempExtractionDir, _cts.Token);

            if (directSuccess)
            {
                return IsoTestResultStatus.Passed;
            }

            // ---------------------------------------------------------
            // ATTEMPT 2: Fallback (Copy to Temp -> Extract)
            // ---------------------------------------------------------
            _logger.LogMessage($"  Direct extraction failed. Falling back to copy-to-temp strategy for '{isoFileName}'...");

            // Clean up temp dir from the failed direct attempt
            if (Directory.Exists(tempExtractionDir))
            {
                await Task.Run(() => Directory.Delete(tempExtractionDir, true), _cts.Token);
            }

            await Task.Run(() => Directory.CreateDirectory(tempExtractionDir), _cts.Token);

            var simpleFilename = GenerateFilename.GenerateSimpleFilename(fileIndex);
            var simpleFilePath = Path.Combine(tempExtractionDir, simpleFilename);

            _logger.LogMessage($"  Copying '{isoFileName}' to simple filename '{simpleFilename}' for testing");
            var copySuccess = await CopyFileWithCloudRetryAsync(isoFilePath, simpleFilePath);
            if (!copySuccess)
            {
                return IsoTestResultStatus.Failed;
            }

            // Run extraction on the local copy via Service
            var extractionSuccess = await _externalToolService.RunIsoExtractionAsync(simpleFilePath, tempExtractionDir, _cts.Token);

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
            if (Directory.Exists(tempExtractionDir))
            {
                await Task.Run(() => Directory.Delete(tempExtractionDir, true), CancellationToken.None);
            }
        }
    }
}