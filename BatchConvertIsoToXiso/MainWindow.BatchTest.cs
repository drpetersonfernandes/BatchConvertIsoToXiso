using System.IO;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private async Task PerformBatchIsoTestAsync(string extractXisoPath, string inputFolder, bool moveSuccessful, string? successFolder, bool moveFailed, string? failedFolder, List<string> isoFilesToTest)
    {
        var topLevelItemsProcessed = 0;

        _uiSuccessCount = 0;
        _uiFailedCount = 0;
        _uiSkippedCount = 0;

        _logger.LogMessage($"Found {isoFilesToTest.Count} .iso files for testing.");
        UpdateSummaryStatsUi(); // Use _uiTotalFiles which is set to isoFilesToTest.Count
        UpdateProgressUi(0, _uiTotalFiles);
        _logger.LogMessage($"Starting test... Total .iso files to test: {isoFilesToTest.Count}.");

        var testFileIndex = 1; // Counter for simple test filenames

        foreach (var isoFilePath in isoFilesToTest)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var isoFileName = Path.GetFileName(isoFilePath);

            UpdateProgressUi(topLevelItemsProcessed, _uiTotalFiles); // Update progress at start of each file
            UpdateStatus($"Testing: {isoFileName}");
            _logger.LogMessage($"Testing ISO: {isoFileName}...");

            // Drive for testing (temp) vs. moving successful (output)
            SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath())); // Test extraction always uses the temp path

            var testStatus = await TestSingleIsoAsync(extractXisoPath, isoFilePath, testFileIndex);
            testFileIndex++;

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

    private async Task<IsoTestResultStatus> TestSingleIsoAsync(string extractXisoPath, string isoFilePath, int fileIndex)
    {
        var isoFileName = Path.GetFileName(isoFilePath);
        var tempExtractionDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_TestExtract", Guid.NewGuid().ToString());
        string? simpleFilePath;

        try
        {
            await Task.Run(() => Directory.CreateDirectory(tempExtractionDir), _cts.Token);
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
            var simpleFilename = GenerateFilename.GenerateSimpleFilename(fileIndex);
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
                if (Directory.Exists(tempExtractionDir))
                {
                    await Task.Run(() => Directory.Delete(tempExtractionDir, true), _cts.Token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"  Error cleaning temp folder for '{isoFileName}': {ex.Message}");
            }
        }
    }
}