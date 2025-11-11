using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;
using SevenZip;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private async Task PerformBatchConversionAsync(string extractXisoPath, string inputFolder, string outputFolder, bool deleteOriginals, bool skipSystemUpdate, List<string> topLevelEntries)
    {
        var tempFoldersToCleanUpAtEnd = new List<string>();
        var archivesFailedToExtractOrProcess = 0;

        UpdateSummaryStatsUi();
        UpdateProgressUi(0, _uiTotalFiles);
        Application.Current.Dispatcher.Invoke(() => ProgressBar.IsIndeterminate = false);
        _logger.LogMessage($"Starting conversion... Total top-level files/archives to process: {_uiTotalFiles}.");

        var globalFileIndex = 1; // Global counter for simple filenames
        var topLevelItemsProcessed = 0; // Counter for progress bar

        foreach (var currentEntryPath in topLevelEntries)
        {
            _cts.Token.ThrowIfCancellationRequested();

            var entryFileName = Path.GetFileName(currentEntryPath);
            UpdateProgressUi(topLevelItemsProcessed, _uiTotalFiles); // Update progress at start of each file
            UpdateStatus($"Processing: {entryFileName}");
            var entryExtension = Path.GetExtension(currentEntryPath).ToLowerInvariant();

            switch (entryExtension)
            {
                case ".iso":
                {
                    _logger.LogMessage($"Processing standalone ISO: {entryFileName}...");
                    var status = await ConvertFileAsync(extractXisoPath, currentEntryPath, outputFolder, deleteOriginals, globalFileIndex, skipSystemUpdate);
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

                    topLevelItemsProcessed++; // Increment for top-level item
                    UpdateSummaryStatsUi();
                    break;
                }
                case ".zip" or ".7z" or ".rar":
                {
                    _logger.LogMessage($"Processing archive: {entryFileName}...");
                    string? currentArchiveTempExtractionDir = null;
                    var archiveExtractedSuccessfully = false;
                    var archiveHadInternalFailures = false;
                    var archiveHadInternalSuccesses = false;
                    var archiveHadInternalSkips = false;

                    try
                    {
                        currentArchiveTempExtractionDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_Extract", Guid.NewGuid().ToString());
                        await Task.Run(() => Directory.CreateDirectory(currentArchiveTempExtractionDir));
                        tempFoldersToCleanUpAtEnd.Add(currentArchiveTempExtractionDir);
                        SetCurrentOperationDrive(GetDriveLetter(Path.GetTempPath()));
                        try
                        {
                            archiveExtractedSuccessfully = await _fileExtractor.ExtractArchiveAsync(currentEntryPath, currentArchiveTempExtractionDir, _cts);
                        }
                        catch (SevenZipLibraryException ex)
                        {
                            _messageBoxService.ShowError($"Error extracting {entryFileName}: Could not load the 7-Zip x64 library. " +
                                                         "Please ensure 7z_x64.dll is in the application folder.");
                            _logger.LogMessage($"Extraction of {entryFileName} failed due to missing 7z_x64.dll: {ex.Message}");
                            archiveExtractedSuccessfully = false;
                        }
                        catch (Exception ex)
                        {
                            _messageBoxService.ShowError($"Error extracting archive {entryFileName}: {ex.Message}");
                            _logger.LogMessage($"Extraction of {entryFileName} failed: {ex.Message}");
                            archiveExtractedSuccessfully = false;
                        }

                        if (archiveExtractedSuccessfully)
                        {
                            var extractedIsoFiles = await Task.Run(() => Directory.GetFiles(currentArchiveTempExtractionDir, "*.iso", SearchOption.AllDirectories), _cts.Token);
                            var extractedCueFiles = await Task.Run(() => Directory.GetFiles(currentArchiveTempExtractionDir, "*.cue", SearchOption.AllDirectories), _cts.Token);
                            var totalFoundFiles = extractedIsoFiles.Length + extractedCueFiles.Length;

                            if (totalFoundFiles == 0) // No relevant files found in archive
                            {
                                _logger.LogMessage($"No ISO or CUE files found in archive: {entryFileName}. Marking archive as skipped.");
                                archiveHadInternalSkips = true; // Mark as skipped if no relevant files found
                            }

                            _logger.LogMessage($"Found {extractedIsoFiles.Length} ISO(s) and {extractedCueFiles.Length} CUE file(s) in {entryFileName}. Processing them now...");

                            foreach (var extractedIsoPath in extractedIsoFiles)
                            {
                                _cts.Token.ThrowIfCancellationRequested();
                                var extractedIsoName = Path.GetFileName(extractedIsoPath);
                                _logger.LogMessage($"  Converting ISO from archive: {extractedIsoName}...");
                                var status = await ConvertFileAsync(extractXisoPath, extractedIsoPath, outputFolder, false, globalFileIndex, skipSystemUpdate); // Pass new option
                                // DO NOT increment _totalProcessedFiles, _uiSuccessCount, _uiFailedCount, _uiSkippedCount here for internal files
                                globalFileIndex++;
                                switch (status)
                                {
                                    case FileProcessingStatus.Converted:
                                        archiveHadInternalSuccesses = true;
                                        break;
                                    case FileProcessingStatus.Skipped:
                                        archiveHadInternalSkips = true;
                                        break;
                                    case FileProcessingStatus.Failed:
                                        archiveHadInternalFailures = true;
                                        _failedConversionFilePaths.Add(extractedIsoPath); // Still log internal failures for detailed report
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
                                        // DO NOT increment _totalProcessedFiles, _uiSuccessCount, _uiFailedCount, _uiSkippedCount here for internal files
                                        globalFileIndex++;
                                        switch (status)
                                        {
                                            case FileProcessingStatus.Converted:
                                                archiveHadInternalSuccesses = true;
                                                break;
                                            case FileProcessingStatus.Skipped:
                                                archiveHadInternalSkips = true;
                                                break;
                                            case FileProcessingStatus.Failed:
                                                archiveHadInternalFailures = true;
                                                _failedConversionFilePaths.Add(extractedCuePath); // Log the original CUE path as the failure source
                                                // _failedConversionFilePaths.Add(extractedCuePath); // Still log internal failures for detailed report
                                                break;
                                        }
                                    }
                                    else
                                    {
                                        archiveHadInternalFailures = true;
                                        _logger.LogMessage($"Failed to convert CUE/BIN to ISO: {extractedCueName}. It will be skipped.");
                                        // _uiFailedCount++; // No direct UI increment for internal files
                                        _failedConversionFilePaths.Add(extractedCuePath);
                                        // _totalProcessedFiles++; // No direct UI increment for internal files
                                    }
                                }
                                finally
                                {
                                    if (tempCueBinDir != null) await CleanupTempFoldersAsync(new List<string> { tempCueBinDir });
                                    // UpdateSummaryStatsUi(); // No need to update UI for internal files
                                }
                            }
                        }
                        else
                        {
                            _logger.LogMessage($"Failed to extract archive: {entryFileName}. It will be skipped.");
                            archiveHadInternalFailures = true; // Mark archive as failed due to extraction failure
                            archivesFailedToExtractOrProcess++;
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
                        archiveHadInternalFailures = true; // Mark archive as failed due to exception
                        archivesFailedToExtractOrProcess++;
                        _failedConversionFilePaths.Add(currentEntryPath);
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

                    // After processing the entire archive (or failing to extract it), update top-level archive status
                    if (archiveHadInternalFailures || !archiveExtractedSuccessfully)
                    {
                        _uiFailedCount++;
                        _failedConversionFilePaths.Add(currentEntryPath); // Add the archive itself to failed list
                    }
                    else if (archiveHadInternalSuccesses)
                    {
                        _uiSuccessCount++;
                    }
                    else if (archiveHadInternalSkips) // All internal were skipped, or no relevant files found
                    {
                        _uiSkippedCount++;
                    }

                    topLevelItemsProcessed++; // Increment for top-level item (archive)
                    UpdateSummaryStatsUi(); // Update UI after processing the entire archive

                    switch (deleteOriginals)
                    {
                        case true when archiveExtractedSuccessfully:
                        {
                            // Only delete the original archive if it was successfully extracted AND all its internal contents were processed without failure.
                            if (!archiveHadInternalFailures)
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
                    }
                    finally
                    {
                        if (tempCueBinDir != null) await CleanupTempFoldersAsync(new List<string> { tempCueBinDir });
                        topLevelItemsProcessed++; // Increment for top-level item (CUE)
                        UpdateSummaryStatsUi();
                    }

                    break;
                }
            }

            UpdateProgressUi(topLevelItemsProcessed, _uiTotalFiles); // Update progress based on top-level items processed
        }

        if (!_cts.Token.IsCancellationRequested)
        {
            UpdateProgressUi(topLevelItemsProcessed, _uiTotalFiles);
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
            var simpleFilename = GenerateFilename.GenerateSimpleFilename(fileIndex);
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
}