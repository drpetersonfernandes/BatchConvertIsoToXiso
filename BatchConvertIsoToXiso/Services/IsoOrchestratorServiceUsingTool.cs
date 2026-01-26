using System.IO;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services.Xiso;

namespace BatchConvertIsoToXiso.Services;

public class IsoOrchestratorServiceUsingTool : IIsoOrchestratorService
{
    private readonly IExternalToolService _externalToolService;
    private readonly IFileExtractor _fileExtractor;
    private readonly IFileMover _fileMover;
    private readonly IBugReportService _bugReportService;
    private readonly INativeIsoIntegrityService _nativeIsoTester;

    private class ProcessingContext
    {
        public int GlobalFileIndex { get; set; } = 1;
    }

    public IsoOrchestratorServiceUsingTool(
        IExternalToolService externalToolService,
        IFileExtractor fileExtractor,
        IFileMover fileMover,
        IBugReportService bugReportService,
        INativeIsoIntegrityService nativeIsoTester)
    {
        _externalToolService = externalToolService;
        _fileExtractor = fileExtractor;
        _fileMover = fileMover;
        _bugReportService = bugReportService;
        _nativeIsoTester = nativeIsoTester;
    }

    #region Conversion Logic

    public async Task ConvertAsync(
        string inputFolder,
        string outputFolder,
        bool deleteOriginals,
        bool skipSystemUpdate,
        bool checkIntegrity,
        bool searchSubfolders,
        IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired,
        CancellationToken token)
    {
        var enumOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = searchSubfolders
        };

        var topLevelEntries = await Task.Run(() => Directory.GetFiles(inputFolder, "*.*", enumOptions)
            .Where(static f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext is ".iso" or ".zip" or ".7z" or ".rar" or ".cue";
            }).ToList(), token);

        if (topLevelEntries.Count == 0) return;

        progress.Report(new BatchOperationProgress { TotalFiles = topLevelEntries.Count });

        var tempFoldersToCleanUp = new List<string>();
        var context = new ProcessingContext();
        var topLevelProcessed = 0;

        foreach (var entryPath in topLevelEntries)
        {
            token.ThrowIfCancellationRequested();

            if (!File.Exists(entryPath))
            {
                progress.Report(new BatchOperationProgress { LogMessage = $"Error: Source file not found: {entryPath}. Skipping.", FailedCount = 1, FailedPathToAdd = entryPath });
                topLevelProcessed++;
                progress.Report(new BatchOperationProgress { ProcessedCount = topLevelProcessed });
                continue;
            }

            var fileName = Path.GetFileName(entryPath);
            var extension = Path.GetExtension(entryPath).ToLowerInvariant();
            progress.Report(new BatchOperationProgress { StatusText = $"Processing: {fileName}", CurrentDrive = PathHelper.GetDriveLetter(entryPath) });

            try
            {
                switch (extension)
                {
                    case ".iso":
                        var isoStatus = await ConvertFileInternalAsync(entryPath, outputFolder, deleteOriginals, context.GlobalFileIndex++, skipSystemUpdate, checkIntegrity, progress, onCloudRetryRequired, token);
                        ReportStatus(isoStatus, entryPath, progress);
                        break;

                    case ".zip" or ".7z" or ".rar":
                        await ProcessArchiveAsync(entryPath, outputFolder, deleteOriginals, skipSystemUpdate, checkIntegrity, context, tempFoldersToCleanUp, progress, onCloudRetryRequired, token);
                        break;

                    case ".cue":
                        await ProcessCueAsync(entryPath, outputFolder, deleteOriginals, skipSystemUpdate, checkIntegrity, context, tempFoldersToCleanUp, progress, onCloudRetryRequired, token);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                progress.Report(new BatchOperationProgress { LogMessage = $"Critical error processing {fileName}: {ex.Message}", FailedCount = 1, FailedPathToAdd = entryPath });
                _ = _bugReportService.SendBugReportAsync($"Orchestrator error on {fileName}: {ex}");
            }

            topLevelProcessed++;
            progress.Report(new BatchOperationProgress { ProcessedCount = topLevelProcessed });
        }

        await CleanupTempFoldersAsync(tempFoldersToCleanUp, progress);
    }

    private async Task ProcessArchiveAsync(string archivePath, string outputFolder, bool deleteOriginal, bool skipUpdate, bool checkIntegrity, ProcessingContext context, List<string> tempFolders, IProgress<BatchOperationProgress> progress, Func<string, Task<CloudRetryResult>> cloudRetry, CancellationToken token)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_Extract", Guid.NewGuid().ToString());
        tempFolders.Add(tempDir);

        bool extracted;
        var internalFail = false;
        var internalSuccess = false;

        try
        {
            Directory.CreateDirectory(tempDir);
            extracted = await _fileExtractor.ExtractArchiveAsync(archivePath, tempDir, CancellationTokenSource.CreateLinkedTokenSource(token));

            if (extracted)
            {
                var files = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories)
                    .Where(static f => Path.GetExtension(f).ToLowerInvariant() is ".iso" or ".cue").ToList();

                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    FileProcessingStatus status;
                    if (Path.GetExtension(file).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        status = await ConvertFileInternalAsync(file, outputFolder, false, context.GlobalFileIndex++, skipUpdate, checkIntegrity, progress, cloudRetry, token);
                    }
                    else
                    {
                        status = await ProcessCueInternalAsync(file, outputFolder, false, skipUpdate, checkIntegrity, context, tempFolders, progress, cloudRetry, token);
                    }

                    switch (status)
                    {
                        case FileProcessingStatus.Converted:
                            internalSuccess = true;
                            break;
                        case FileProcessingStatus.Failed:
                            internalFail = true;
                            break;
                    }
                }
            }
            else
            {
                internalFail = true;
            }
        }
        finally
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempDir, 3, 1000, null);
            tempFolders.Remove(tempDir);
        }

        if (internalFail || !extracted) progress.Report(new BatchOperationProgress { FailedCount = 1, FailedPathToAdd = archivePath });
        else if (internalSuccess) progress.Report(new BatchOperationProgress { SuccessCount = 1 });
        else progress.Report(new BatchOperationProgress { SkippedCount = 1 });

        if (deleteOriginal && extracted && !internalFail)
        {
            try
            {
                File.Delete(archivePath);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private async Task ProcessCueAsync(string cuePath, string outputFolder, bool deleteOriginal, bool skipUpdate, bool checkIntegrity, ProcessingContext context, List<string> tempFolders, IProgress<BatchOperationProgress> progress, Func<string, Task<CloudRetryResult>> cloudRetry, CancellationToken token)
    {
        var status = await ProcessCueInternalAsync(cuePath, outputFolder, deleteOriginal, skipUpdate, checkIntegrity, context, tempFolders, progress, cloudRetry, token);
        ReportStatus(status, cuePath, progress);
    }

    private async Task<FileProcessingStatus> ProcessCueInternalAsync(string cuePath, string outputFolder, bool deleteOriginal, bool skipUpdate, bool checkIntegrity, ProcessingContext context, List<string> tempFolders, IProgress<BatchOperationProgress> progress, Func<string, Task<CloudRetryResult>> cloudRetry, CancellationToken token)
    {
        var tempCueDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_CueBin", Guid.NewGuid().ToString());
        tempFolders.Add(tempCueDir);
        try
        {
            Directory.CreateDirectory(tempCueDir);
            var tempIso = await _externalToolService.ConvertCueBinToIsoAsync(cuePath, tempCueDir, token);
            if (tempIso != null && File.Exists(tempIso))
            {
                var status = await ConvertFileInternalAsync(tempIso, outputFolder, false, context.GlobalFileIndex++, skipUpdate, checkIntegrity, progress, cloudRetry, token);
                if (deleteOriginal && status != FileProcessingStatus.Failed)
                {
                    try
                    {
                        File.Delete(cuePath);
                        var bin = Path.ChangeExtension(cuePath, ".bin");
                        if (File.Exists(bin)) File.Delete(bin);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                return status;
            }

            return FileProcessingStatus.Failed;
        }
        finally
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempCueDir, 3, 1000, null);
            tempFolders.Remove(tempCueDir);
        }
    }

    private async Task<FileProcessingStatus> ConvertFileInternalAsync(string inputFile, string outputFolder, bool deleteOriginal, int fileIndex, bool skipSystemUpdate, bool checkIntegrity, IProgress<BatchOperationProgress> progress, Func<string, Task<CloudRetryResult>> onCloudRetryRequired, CancellationToken token)
    {
        var originalFileName = Path.GetFileName(inputFile);
        string? localTempWorkingDir = null;

        try
        {
            localTempWorkingDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_Convert", Guid.NewGuid().ToString());
            Directory.CreateDirectory(localTempWorkingDir);

            var simpleFilename = GenerateFilename.GenerateSimpleFilename(fileIndex);
            var localTempIsoPath = Path.Combine(localTempWorkingDir, simpleFilename);

            progress.Report(new BatchOperationProgress { LogMessage = $"File '{originalFileName}': Copying to local temp...", CurrentDrive = PathHelper.GetDriveLetter(Path.GetTempPath()) });

            var copySuccess = await CopyFileWithCloudRetryAsync(inputFile, localTempIsoPath, onCloudRetryRequired, progress, token);
            if (!copySuccess) return FileProcessingStatus.Failed;

            var toolResult = await _externalToolService.RunConversionAsync(localTempIsoPath, skipSystemUpdate, token);

            if (toolResult == ConversionToolResultStatus.Failed) return FileProcessingStatus.Failed;

            Directory.CreateDirectory(outputFolder);
            var destinationPath = Path.Combine(outputFolder, originalFileName);
            var isTempFile = inputFile.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

            progress.Report(new BatchOperationProgress { LogMessage = $"File '{originalFileName}': Moving to output...", CurrentDrive = PathHelper.GetDriveLetter(outputFolder) });
            await _fileMover.RobustMoveFileAsync(localTempIsoPath, destinationPath, token);

            if (checkIntegrity)
            {
                progress.Report(new BatchOperationProgress { LogMessage = $"File '{originalFileName}': Verifying output integrity..." });
                var isValid = await _nativeIsoTester.TestIsoIntegrityAsync(destinationPath, new Progress<BatchOperationProgress>(), token);
                if (!isValid)
                {
                    progress.Report(new BatchOperationProgress { LogMessage = $"[ERROR] File '{originalFileName}': Output failed integrity check. Deleting corrupt output." });
                    try
                    {
                        if (File.Exists(destinationPath)) File.Delete(destinationPath);
                    }
                    catch
                    {
                        /* ignore */
                    }

                    return FileProcessingStatus.Failed;
                }

                progress.Report(new BatchOperationProgress { LogMessage = $"File '{originalFileName}': Integrity verified." });
            }

            if (deleteOriginal && !isTempFile)
            {
                try
                {
                    File.Delete(inputFile);
                    progress.Report(new BatchOperationProgress { LogMessage = $"Deleted original: {originalFileName}" });
                }
                catch (Exception ex)
                {
                    progress.Report(new BatchOperationProgress { LogMessage = $"Warning: Could not delete original {originalFileName}: {ex.Message}" });
                }
            }

            return toolResult == ConversionToolResultStatus.Skipped ? FileProcessingStatus.Skipped : FileProcessingStatus.Converted;
        }
        finally
        {
            if (localTempWorkingDir != null)
                await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(localTempWorkingDir, 5, 1000, null);
        }
    }

    #endregion

    #region Testing Logic

    public async Task TestAsync(string inputFolder, bool moveSuccessful, bool moveFailed, bool searchSubfolders, IProgress<BatchOperationProgress> progress, Func<string, Task<CloudRetryResult>> onCloudRetryRequired, CancellationToken token)
    {
        var enumOptions = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = searchSubfolders };
        var isoFiles = await Task.Run(() => Directory.GetFiles(inputFolder, "*.iso", enumOptions).ToList(), token);

        if (isoFiles.Count == 0) return;

        progress.Report(new BatchOperationProgress { TotalFiles = isoFiles.Count });
        var successFolder = Path.Combine(inputFolder, "_success");
        var failedFolder = Path.Combine(inputFolder, "_failed");

        var processed = 0;
        var fileIndex = 1;

        foreach (var isoPath in isoFiles)
        {
            token.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(isoPath);
            progress.Report(new BatchOperationProgress { StatusText = $"Testing: {fileName}", CurrentDrive = PathHelper.GetDriveLetter(Path.GetTempPath()) });

            var result = await TestSingleIsoInternalAsync(isoPath, fileIndex++, onCloudRetryRequired, progress, token);

            if (result == IsoTestResultStatus.Passed)
            {
                progress.Report(new BatchOperationProgress { SuccessCount = 1, LogMessage = $"  SUCCESS: '{fileName}' passed test." });
                if (moveSuccessful) await _fileMover.MoveTestedFileAsync(isoPath, successFolder, "successfully tested", token);
            }
            else
            {
                progress.Report(new BatchOperationProgress { FailedCount = 1, FailedPathToAdd = isoPath, LogMessage = $"  FAILURE: '{fileName}' failed test." });
                if (moveFailed) await _fileMover.MoveTestedFileAsync(isoPath, failedFolder, "failed test", token);
            }

            processed++;
            progress.Report(new BatchOperationProgress { ProcessedCount = processed });
        }
    }

    private async Task<IsoTestResultStatus> TestSingleIsoInternalAsync(string isoPath, int index, Func<string, Task<CloudRetryResult>> cloudRetry, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "BatchConvertIsoToXiso_TestExtract", Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(tempDir);
            var success = await _externalToolService.RunIsoExtractionAsync(isoPath, tempDir, token);
            if (success) return IsoTestResultStatus.Passed;

            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempDir, 3, 500, null);
            Directory.CreateDirectory(tempDir);

            var simpleName = GenerateFilename.GenerateSimpleFilename(index);
            var localPath = Path.Combine(tempDir, simpleName);

            if (await CopyFileWithCloudRetryAsync(isoPath, localPath, cloudRetry, progress, token))
            {
                return await _externalToolService.RunIsoExtractionAsync(localPath, tempDir, token)
                    ? IsoTestResultStatus.Passed
                    : IsoTestResultStatus.Failed;
            }

            return IsoTestResultStatus.Failed;
        }
        catch
        {
            return IsoTestResultStatus.Failed;
        }
        finally
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempDir, 3, 500, null);
        }
    }

    #endregion

    #region Helpers

    private static void ReportStatus(FileProcessingStatus status, string path, IProgress<BatchOperationProgress> progress)
    {
        switch (status)
        {
            case FileProcessingStatus.Converted: progress.Report(new BatchOperationProgress { SuccessCount = 1 }); break;
            case FileProcessingStatus.Skipped: progress.Report(new BatchOperationProgress { SkippedCount = 1 }); break;
            case FileProcessingStatus.Failed: progress.Report(new BatchOperationProgress { FailedCount = 1, FailedPathToAdd = path }); break;
        }
    }

    private static async Task<bool> CopyFileWithCloudRetryAsync(string source, string dest, Func<string, Task<CloudRetryResult>> cloudRetry, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        while (true)
        {
            try
            {
                await Task.Run(() => File.Copy(source, dest, true), token);
                return true;
            }
            catch (IOException ex) when (ex.Message.Contains("cloud operation", StringComparison.OrdinalIgnoreCase))
            {
                var result = await cloudRetry(Path.GetFileName(source));
                switch (result)
                {
                    case CloudRetryResult.Retry:
                        continue;
                    case CloudRetryResult.Cancel:
                        throw new OperationCanceledException();
                    default:
                        return false;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                progress.Report(new BatchOperationProgress { LogMessage = $"Copy failed: {ex.Message}" });
                return false;
            }
        }
    }

    private static async Task CleanupTempFoldersAsync(List<string> folders, IProgress<BatchOperationProgress> progress)
    {
        if (folders.Count == 0) return;

        progress.Report(new BatchOperationProgress { LogMessage = "Cleaning up temporary folders..." });
        foreach (var folder in folders.ToList())
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(folder, 5, 1000, null);
        }
    }

    #endregion
}