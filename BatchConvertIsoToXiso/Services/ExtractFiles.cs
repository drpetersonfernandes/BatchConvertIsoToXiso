using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BatchConvertIsoToXiso.Interfaces;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace BatchConvertIsoToXiso.Services;

public class FileExtractorService : IFileExtractor
{
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly string _sevenZipExePath;

    private static string? FindSevenZipExe()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;

        var archExeName = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "7za.exe",
            Architecture.Arm64 => "7za_arm64.exe",
            _ => null
        };

        if (archExeName != null)
        {
            var archExe = Path.Combine(appDir, archExeName);
            if (File.Exists(archExe))
                return archExe;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var searchPaths = new[]
        {
            Path.Combine(programFiles, "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? programFiles, "7-Zip", "7z.exe")
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    // Cloud file attribute constants
    private const int FileAttributeRecallOnOpen = 0x00040000;
    private const int FileAttributeRecallOnDataAccess = 0x00400000;
    private const int ErrorCloudFileProviderNotRunning = 362;

    public FileExtractorService(ILogger logger, IBugReportService bugReportService)
    {
        _logger = logger;
        _bugReportService = bugReportService;
        _sevenZipExePath = FindSevenZipExe() ?? string.Empty;
    }

    /// <summary>
    /// Checks if a file is a cloud file (OneDrive, Dropbox, etc.) and not fully downloaded locally.
    /// </summary>
    private static bool IsCloudFile(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists) return false;

            var attributes = fileInfo.Attributes;
            return (attributes & (FileAttributes)FileAttributeRecallOnOpen) == (FileAttributes)FileAttributeRecallOnOpen ||
                   (attributes & (FileAttributes)FileAttributeRecallOnDataAccess) == (FileAttributes)FileAttributeRecallOnDataAccess;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to ensure a cloud file is hydrated (downloaded locally) before accessing it.
    /// </summary>
    private async Task<bool> EnsureCloudFileHydratedAsync(string filePath, CancellationToken token)
    {
        try
        {
            // Open the file with read access to trigger hydration
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);

            // Read a small portion to ensure the file is fully available
            var buffer = new byte[1];
            _ = await fs.ReadAsync(buffer, token);

            return true;
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070146) || // ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING
                                     ex.Message.Contains("cloud file provider", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines if an exception is related to cloud file provider issues.
    /// </summary>
    private static bool IsCloudFileProviderError(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            // Check for specific cloud file error codes and messages
            if (ioEx.HResult == unchecked((int)0x80070146) || // ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING
                ioEx.HResult == ErrorCloudFileProviderNotRunning)
            {
                return true;
            }

            if (ioEx.Message.Contains("cloud file provider", StringComparison.OrdinalIgnoreCase) ||
                ioEx.Message.Contains("cloud file", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Verifies that the drive containing the specified path is ready.
    /// </summary>
    private void VerifyDriveReady(string filePath)
    {
        try
        {
            var root = Path.GetPathRoot(filePath);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    throw new IOException($"The device is not ready. : '{filePath}'");
                }
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"  Warning: Could not verify drive readiness: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes an asynchronous action with a brief retry on IOException.
    /// </summary>
    private async Task ExecuteWithRetryAsync(Func<Task> action, string operationDescription, CancellationToken token)
    {
        const int maxRetries = 3;
        var attempt = 0;

        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (IOException ex) when (attempt < maxRetries)
            {
                attempt++;
                var delayMs = 1000 * (1 << (attempt - 1));
                _logger.LogMessage($"  {operationDescription} failed on attempt {attempt}/{maxRetries}: {ex.Message}. Retrying in {delayMs / 1000}s...");
                await Task.Delay(delayMs, token);
            }
        }
    }

    /// <summary>
    /// Checks if there is enough disk space on the target drive for the extraction.
    /// </summary>
    private void CheckDiskSpace(string extractionPath, long totalSize, string archiveFileName)
    {
        try
        {
            var fullPath = Path.GetFullPath(extractionPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return;

            var drive = new DriveInfo(root);
            var requiredWithBuffer = totalSize + Math.Max(totalSize / 10, 200L * 1024 * 1024);
            if (drive.AvailableFreeSpace < requiredWithBuffer)
            {
                var requiredSpace = Formatter.FormatBytes(totalSize);
                var availableSpace = Formatter.FormatBytes(drive.AvailableFreeSpace);
                var errorMessage = $"Not enough disk space to extract {archiveFileName}. Required: {requiredSpace} ({totalSize:N0} bytes), Available: {availableSpace} ({drive.AvailableFreeSpace:N0} bytes), with safety buffer requires: {Formatter.FormatBytes(requiredWithBuffer)}.";
                _logger.LogMessage($"  ERROR: {errorMessage}");
                throw new IOException(errorMessage);
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"  Warning: Could not check disk space: {ex.Message}");
        }
    }

    public async Task<(long TotalUncompressedSize, int FileCount)> GetArchiveInfoAsync(string archivePath, CancellationToken token)
    {
        var (totalSize, fileCount) = await Task.Run(() =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var entries = archive.Entries.Where(static e => !e.IsDirectory).ToList();
            var count = entries.Count;
            var size = entries.Sum(static e => e.Size);
            return (size, count);
        }, token);

        return (totalSize, fileCount);
    }

    private static bool IsFileLocked(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) // ERROR_SHARING_VIOLATION
        {
            return true;
        }
    }

    private async Task<bool> TryExtractWithSevenZipCliAsync(string archivePath, string extractionPath, CancellationToken token)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        _logger.LogMessage($"  Extracting with 7-Zip CLI: {archiveFileName}");

        try
        {
            var args = $"x \"{archivePath}\" -o\"{extractionPath}\" -y";
            var exeDir = Path.GetDirectoryName(_sevenZipExePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _sevenZipExePath,
                Arguments = args,
                WorkingDirectory = exeDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);

            await using (token.Register(static state =>
                         {
                             var p = (Process?)state;
                             if (p is { HasExited: false })
                             {
                                 try
                                 {
                                     p.Kill();
                                 }
                                 catch
                                 {
                                     // ignored
                                 }
                             }
                         }, process))
            {
                await process.WaitForExitAsync(token);
            }

            var errors = await stderrTask;

            try
            {
                await stdoutTask;
            }
            catch
            {
                /* stdout read failure is non-critical */
            }

            if (process.ExitCode == 0)
            {
                _logger.LogMessage($"  Successfully extracted using 7-Zip CLI: {archiveFileName}");
                return true;
            }

            _logger.LogMessage($"  7-Zip CLI returned exit code {process.ExitCode}: {errors}");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"  7-Zip CLI fallback failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ExtractArchiveAsync(string archivePath, string extractionPath, CancellationToken token)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        _logger.LogMessage($"Starting extraction: {archiveFileName}");
        _logger.LogMessage($"  Extraction target: {extractionPath}");

        try
        {
            // Check for cloud files and attempt to hydrate before extraction
            if (IsCloudFile(archivePath))
            {
                _logger.LogMessage($"  Detected cloud file: {archiveFileName}. Attempting to ensure local availability...");

                var hydrated = await EnsureCloudFileHydratedAsync(archivePath, token);
                if (!hydrated)
                {
                    throw new IOException(
                        $"The cloud file provider is not running. The file '{archivePath}' is stored in a cloud storage service " +
                        "(OneDrive, Dropbox, etc.) but the sync client is not available. Please ensure your cloud storage " +
                        "application is running and the file is fully synchronized before trying again.");
                }

                _logger.LogMessage("  Cloud file is now available locally.");
            }

            _logger.LogMessage($"  Analyzing archive: {archiveFileName}...");

            VerifyDriveReady(archivePath);

            if (IsFileLocked(archivePath))
            {
                throw new IOException(
                    $"The file '{archiveFileName}' is currently in use by another process. " +
                    "Please close any programs that may have the file open (file explorer, zip tools, antivirus, download manager, etc.) and try again.");
            }

            await ExecuteWithRetryAsync(() =>
                {
                    return Task.Run(() =>
                    {
                        try
                        {
                            using var archive = ArchiveFactory.OpenArchive(archivePath);
                            var entries = archive.Entries.Where(static e => !e.IsDirectory).ToList();
                            var fileCount = entries.Count;
                            var totalSize = entries.Sum(static e => e.Size);
                            var archiveFormat = archive.Type;

                            _logger.LogMessage($"  Archive format: {archiveFormat}, Files to extract: {fileCount}, Total size: {Formatter.FormatBytes(totalSize)}");

                            CheckDiskSpace(extractionPath, totalSize, archiveFileName);

                            _logger.LogMessage($"  Extracting files from {archiveFileName}...");

                            var isoExtracted = false;

                            // Manually extract files to prevent "Zip Slip" (absolute paths or path traversal in archives)
                            foreach (var entry in entries)
                            {
                                token.ThrowIfCancellationRequested();

                                var entryPath = entry.Key;

                                // Strict Zip Slip check: Skip suspicious paths entirely
                                if (entryPath != null && (Path.IsPathRooted(entryPath) || entryPath.Split('\\', '/').Any(static p => p == "..")))
                                {
                                    _logger.LogMessage($"  WARNING: Skipping entry '{entryPath}' - potential path traversal (Zip Slip) detected.");
                                    continue;
                                }

                                // Check for multiple ISOs
                                if (entryPath != null && Path.GetExtension(entryPath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (isoExtracted)
                                    {
                                        _logger.LogMessage($"  Skipping additional ISO: {entryPath} (Only the first ISO is processed per archive).");
                                        continue;
                                    }

                                    isoExtracted = true;
                                }

                                if (entryPath != null)
                                {
                                    var fullDestPath = Path.GetFullPath(Path.Combine(extractionPath, entryPath));

                                    // Ensure the resulting path is still inside our extraction directory
                                    // Fix: base path must end with directory separator to prevent bypass via similar-named directories
                                    var basePath = Path.GetFullPath(extractionPath);
                                    if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                                    {
                                        basePath += Path.DirectorySeparatorChar;
                                    }

                                    if (!fullDestPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        _logger.LogMessage($"  WARNING: Skipping entry '{entryPath}' - potential path traversal (Zip Slip) detected.");
                                        continue;
                                    }

                                    Directory.CreateDirectory(Path.GetDirectoryName(fullDestPath) ?? throw new InvalidOperationException("fullDestPath cannot be null"));
                                    using var fs = new FileStream(fullDestPath, FileMode.Create, FileAccess.Write);
                                    entry.WriteTo(fs);
                                }
                            }
                        }
                        catch (NotSupportedException notSupportedEx) when (
                            notSupportedEx.Message.Contains("Unsupported compression method", StringComparison.OrdinalIgnoreCase))
                        {
                            // SharpCompress doesn't support this compression method (e.g., ZSTD/method 21).
                            // Fall back to 7-Zip CLI.
                            _logger.LogMessage($"  SharpCompress doesn't support this compression method ({notSupportedEx.Message}). Falling back to 7-Zip CLI...");

                            if (!File.Exists(_sevenZipExePath))
                            {
                                throw new IOException(
                                    "This archive uses a compression method (e.g., ZSTD) not supported by the built-in extractor.\n\n" +
                                    "To extract this file automatically, you can:\n" +
                                    "1. Install 7-Zip from https://7-zip.org/ — the app auto-detects it in Program Files.\n" +
                                    "2. Alternatively, place '7za.exe' (for x64) or '7za_arm64.exe' (for ARM64) in the application directory.\n\n" +
                                    "Alternatively, you can manually extract the archive and place the ISO file directly in the input folder.",
                                    notSupportedEx);
                            }

                            // Use synchronous extraction since we're already in Task.Run
                            var args = $"x \"{archivePath}\" -o\"{extractionPath}\" -y";
                            var exeDir = Path.GetDirectoryName(_sevenZipExePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                            using var process = new Process();
                            process.StartInfo = new ProcessStartInfo
                            {
                                FileName = _sevenZipExePath,
                                Arguments = args,
                                WorkingDirectory = exeDir,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };

                            process.Start();
                            var stderrTask = process.StandardError.ReadToEndAsync();
                            _ = process.StandardOutput.ReadToEnd();
                            process.WaitForExit();
                            var stderr = stderrTask.GetAwaiter().GetResult();

                            if (process.ExitCode != 0)
                            {
                                throw new IOException($"7-Zip CLI extraction failed with exit code {process.ExitCode}: {stderr}");
                            }

                            _logger.LogMessage($"  Successfully extracted using 7-Zip CLI fallback: {archiveFileName}");
                        }
                    }, token);
                }, $"Extraction of {archiveFileName}", token);

            _logger.LogMessage($"  Successfully extracted: {archiveFileName}");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogMessage($"Extraction of {archiveFileName} was canceled.");
            throw;
        }
        catch (Exception ex) when (Path.GetExtension(archivePath).Equals(".7z", StringComparison.OrdinalIgnoreCase) &&
                                   (ex is ArchiveException or ArchiveOperationException ||
                                    (ex is InvalidOperationException ioe && ioe.Message.Contains("Archive", StringComparison.OrdinalIgnoreCase))))
        {
            _logger.LogMessage($"  SharpCompress unable to extract 7z ({ex.GetType().Name}), falling back to 7-Zip CLI...");

            if (!File.Exists(_sevenZipExePath))
            {
                const string userMessage = "7z archives require the 7-Zip command-line tool.\n\n" +
                                           "To extract .7z files automatically, you can:\n" +
                                           "1. Install 7-Zip from https://7-zip.org/ — the app auto-detects it in Program Files.\n" +
                                           "2. Alternatively, place '7za.exe' (for x64) or '7za_arm64.exe' (for ARM64) in the application directory.";
                _logger.LogMessage($"  ERROR: {userMessage}");
                throw new IOException(userMessage);
            }

            var cliResult = await TryExtractWithSevenZipCliAsync(archivePath, extractionPath, token);
            if (cliResult)
            {
                _logger.LogMessage($"  Successfully extracted: {archiveFileName}");
                return true;
            }

            _logger.LogMessage($"  Extraction failed for {archiveFileName}.");
            return false;
        }
        catch (NotSupportedException notSupportedEx) when (
            notSupportedEx.Message.Contains("Unsupported compression method", StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // ZIP archives using newer compression methods (e.g., method 21 = ZSTD) are not supported by SharpCompress.
            // Fall back to 7-Zip CLI which supports these methods.
            _logger.LogMessage($"  ZIP archive uses unsupported compression method ({notSupportedEx.Message}). Falling back to 7-Zip CLI...");

            if (!File.Exists(_sevenZipExePath))
            {
                const string userMessage = "This ZIP archive uses a compression method (e.g., ZSTD) not supported by the built-in extractor.\n\n" +
                                           "To extract this file automatically, you can:\n" +
                                           "1. Install 7-Zip from https://7-zip.org/ — the app auto-detects it in Program Files.\n" +
                                           "2. Alternatively, place '7za.exe' (for x64) or '7za_arm64.exe' (for ARM64) in the application directory.\n\n" +
                                           "Alternatively, you can manually extract the ZIP and place the ISO file directly in the input folder.";
                _logger.LogMessage($"  ERROR: {userMessage}");
                throw new IOException(userMessage, notSupportedEx);
            }

            var cliResult = await TryExtractWithSevenZipCliAsync(archivePath, extractionPath, token);
            if (cliResult)
            {
                _logger.LogMessage($"  Successfully extracted: {archiveFileName}");
                return true;
            }

            _logger.LogMessage($"  Extraction failed for {archiveFileName}.");
            return false;
        }
        catch (InvalidFormatException ex)
        {
            var userMessage = $"Error extracting {archiveFileName}: The archive uses a compression format not supported by SharpCompress.\n" +
                              "Please ensure the file is a valid archive or use an alternative extraction tool.\n" +
                              $"Exception: {ex.Message}";
            _logger.LogMessage($"  {userMessage}");
            throw new IOException(userMessage, ex);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Archive"))
        {
            var errorMessage = $"Error extracting {archiveFileName}: Could not open the archive.\n" +
                               "The archive may be corrupted or in an unsupported format.\n" +
                               $"Exception: {ex.Message}";
            _logger.LogMessage($"  {errorMessage}");
            throw;
        }
        catch (Exception ex) when (ex is ArchiveException or ArchiveOperationException)
        {
            var errorMessage = $"Error extracting {archiveFileName}: The archive appears to be invalid, corrupted, or in an unsupported format.\n" +
                               "Please ensure the file is a valid archive (Zip, Rar, 7Zip, etc.).\n" +
                               $"Exception: {ex.Message}";
            _logger.LogMessage($"  {errorMessage}");
            throw new IOException(errorMessage, ex);
        }
        catch (IOException ex) when (ex.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
        {
            var userMessage = $"ERROR: Not enough disk space to extract {archiveFileName}.\n\n" +
                              "Please free up some space on your drive and try again.";
            _logger.LogMessage($"  {userMessage}");
            throw new IOException(userMessage, ex);
        }
        catch (IOException ex) when (IsCloudFileProviderError(ex))
        {
            // Provide user-friendly message for cloud file provider errors
            var userMessage = $"ERROR: Cannot access {archiveFileName} because it is stored in cloud storage (OneDrive, Dropbox, etc.) " +
                              "and the cloud sync provider is not running or the file is not fully synchronized.\n\n" +
                              "Please try:\n" +
                              "1. Ensure your cloud storage application (OneDrive, Dropbox, etc.) is running\n" +
                              "2. Make sure the file is fully downloaded/synced to your local machine\n" +
                              "3. Right-click the file in File Explorer and select 'Always keep on this device'\n" +
                              "4. Try again once the file shows a solid checkmark (not a cloud icon)";

            _logger.LogMessage($"  {userMessage}");

            // Cloud file errors are environmental, do not report as bugs
            throw new IOException(userMessage, ex);
        }
        catch (CryptographicException cryptoEx)
        {
            _logger.LogMessage($"ERROR: {archiveFileName} is encrypted/password-protected. This application cannot extract password-protected archives. Please extract the archive manually using a tool that supports passwords (e.g., WinRAR, 7-Zip) and re-package it without encryption.");

            _ = _bugReportService.SendBugReportAsync($"Error extracting {archiveFileName}: Encrypted archive detected (password-protected).", cryptoEx);

            return false;
        }
        catch (Exception ex)
        {
            // Provide user-friendly message for corrupt archives
            if (ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogMessage($"ERROR: The file '{archiveFileName}' is currently in use by another process. Close any programs that may have the file open (file explorer preview, zip tools, antivirus, download manager, etc.) and try again.");
            }
            else if (ex.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogMessage($"ERROR: {archiveFileName} appears to be corrupt or incomplete. The file may have been damaged during download or transfer. Please re-download the archive and try again.");
            }
            else if (ex.Message.Contains("Bad state", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogMessage($"ERROR: {archiveFileName} appears to be corrupt (invalid compression data). The file may have been damaged during download or transfer. Please re-download the archive and try again.");
            }
            else if (ex.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogMessage($"ERROR: Not enough disk space to extract {archiveFileName}. Please free up some space on your drive and try again.");
            }
            else if (PathHelper.IsNetworkError(ex))
            {
                _logger.LogMessage($"ERROR: Network error while extracting {archiveFileName}. The file may be on a network drive that is no longer available or experiencing connectivity issues.\n\n" +
                    "Please try:\n" +
                    "1. Check that the network drive is still connected and accessible\n" +
                    "2. Copy the file to a local drive before processing\n" +
                    "3. Check your network connection stability\n" +
                    "4. If using WiFi, try a wired connection for better reliability");
            }
            else
            {
                _logger.LogMessage($"Error extracting {archiveFileName}: {ex.Message}");
            }

            // Filter out environmental/hardware errors (disconnected drives, file locks, etc.)
            var isEnvironmentalError = ex is IOException ioEx &&
                                       (ioEx.Message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                                        (ioEx.Message.Contains("device", StringComparison.OrdinalIgnoreCase) &&
                                         !ioEx.Message.Contains("device is not ready", StringComparison.OrdinalIgnoreCase)) ||
                                        ioEx.Message.Contains("Netzwerk", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("réseau", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("la red", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("de red", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("rete", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("no longer available", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("nicht mehr verfügbar", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("n'est plus disponible", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase) ||
                                        ioEx.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase));

            // Filter out common archive errors (corruption, wrong password, etc.) from bug reports
            // Note: Cloud file errors are now reported as they indicate potential app compatibility issues
            if (!isEnvironmentalError &&
                ex is not ArchiveException &&
                ex is not ArchiveOperationException &&
                !ex.Message.Contains("Data error", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Invalid archive", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Unsupported archive", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Bad state", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                _ = _bugReportService.SendBugReportAsync($"Error extracting {archiveFileName}", ex);
            }

            throw;
        }
    }
}
