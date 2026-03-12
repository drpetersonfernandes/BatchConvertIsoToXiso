using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BatchConvertIsoToXiso.interfaces;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;
using BatchConvertIsoToXiso.Services.XisoServices.XDVDFS;
using Microsoft.Win32.SafeHandles;

namespace BatchConvertIsoToXiso.Services.XisoServices;

/// <summary>
/// Provides functionality for rewriting ISO files into XISO format.
/// </summary>
public class XisoWriter
{
    private readonly ILogger _logger;
    private readonly INativeIsoIntegrityService _integrityService;
    private static readonly long[] XisoOffset = [0x18300000, 0xFD90000, 0x89D80000, 0x2080000];
    private static readonly long[] XisoLength = [0x1A2DB0000, 0x1B3880000, 0xBF8A0000, 0x204510000];
    private static readonly long[] RedumpIsoLength = [0x1D26A8000, 0x1D3301800, 0x1D2FEF800, 0x1D3082000, 0x1D3390000, 0x1D31A0000, 0x208E05800, 0x208E03800];

    // P/Invoke for sparse file support (Improvement #1)
    private const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    private static void EnableSparseFile(FileStream fileStream)
    {
        if (OperatingSystem.IsWindows())
        {
            DeviceIoControl(fileStream.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
    }

    public XisoWriter(ILogger logger, INativeIsoIntegrityService integrityService)
    {
        _logger = logger;
        _integrityService = integrityService;
    }

    public Task<FileProcessingStatus> RewriteIsoAsync(string sourcePath, string destPath, bool skipSystemUpdate, bool checkIntegrity, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        return Task.Run(async () =>
        {
            try
            {
                FileInfo isoInfo = new(sourcePath);
                var isoSize = isoInfo.Length;
                var redumpIsoType = Array.IndexOf(RedumpIsoLength, isoSize);

                long inputOffset;
                long targetXisoLength;
                var signatureScanned = false;

                if (redumpIsoType >= 0)
                {
                    var xgdType = redumpIsoType switch
                    {
                        0 => 0, // XGD1
                        1 or 2 or 3 or 4 => 1, // XGD2
                        5 => 2, // XGD2 Hybrid
                        6 or 7 => 3, // XGD3
                        _ => 0
                    };
                    inputOffset = XisoOffset[xgdType];
                    targetXisoLength = XisoLength[xgdType];
                    _logger.LogMessage($"Detected Redump ISO (XGD{xgdType}). Extracting game partition...");
                }
                else
                {
                    // If it's not a known Redump size, treat it as an XISO (Standard or already trimmed)
                    inputOffset = 0;
                    targetXisoLength = isoSize;
                }

                await using FileStream isoFs = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                // Validate the detected offset by checking for XISO signature
                // If signature not found at expected offset, scan for it
                if (!Xdvdfs.ValidateXisoSignatureAtOffset(isoFs, inputOffset))
                {
                    _logger.LogMessage($"XISO signature not found at expected offset 0x{inputOffset:X}. Scanning for game partition...");
                    var detectedOffset = Xdvdfs.FindXisoSignatureOffset(isoFs);
                    switch (detectedOffset)
                    {
                        case > 0:
                            inputOffset = detectedOffset.Value;
                            targetXisoLength = isoSize - inputOffset;
                            signatureScanned = true;
                            _logger.LogMessage($"Found game partition at offset 0x{inputOffset:X} ({inputOffset / (1024 * 1024)} MB). Extracting...");
                            break;
                        case 0 when redumpIsoType < 0:
                            _logger.LogMessage("XISO signature found at start of file (already trimmed or standard XISO).");
                            break;
                        default:
                            if (redumpIsoType >= 0)
                            {
                                // Was detected as Redump but signature not found - try using the offset anyway
                                _logger.LogMessage($"Warning: XISO signature not found, but file size matches known Redump format. Attempting extraction at offset 0x{inputOffset:X}...");
                            }
                            else
                            {
                                _logger.LogMessage("No XISO signature found. Treating as standard XISO...");
                            }

                            break;
                    }
                }

                // Generate valid ranges based on XDVDFS traversal
                List<(uint Start, uint End)> validRanges;
                try
                {
                    validRanges = Xdvdfs.GetXisoRanges(isoFs, inputOffset, true, skipSystemUpdate);
                }
                catch (Exception ex)
                {
                    _logger.LogMessage($"[ERROR] '{Path.GetFileName(sourcePath)}' is not a valid Xbox ISO image. Details: {ex.Message}");
                    return FileProcessingStatus.Failed;
                }

                // If only the header sectors were found, it's not a valid Xbox ISO
                if (validRanges.Count <= 1)
                {
                    _logger.LogMessage($"[ERROR] '{Path.GetFileName(sourcePath)}' contains no valid Xbox filesystem.");
                    return FileProcessingStatus.Failed;
                }

                var lastValidSector = validRanges.Count > 0 ? validRanges[^1].End : 0;
                var headerOffsetSector = (inputOffset + 0x10000) / Utils.SectorSize;

                // Improvement #4: More precise already-optimized check with sector alignment validation
                var expectedOptimizedSize = (lastValidSector + 1) * Utils.SectorSize;
                var isExactSize = isoSize == expectedOptimizedSize;
                var isWithinSector = isoSize <= expectedOptimizedSize && isoSize > (lastValidSector * Utils.SectorSize);
                if (inputOffset == 0 && (isExactSize || isWithinSector))
                {
                    _logger.LogMessage($"[INFO] File '{Path.GetFileName(sourcePath)}' is already optimized. Skipping.");
                    return FileProcessingStatus.AlreadyOptimized;
                }

                if (signatureScanned)
                {
                    _logger.LogMessage("Extracting game partition using detected signature...");
                }
                else if (inputOffset == 0)
                {
                    _logger.LogMessage("Detected XISO. Trimming...");
                }
                else
                {
                    _logger.LogMessage("Detected Redump ISO. Extracting...");
                }

                {
                    await using FileStream xisoFs = new(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

                    // Improvement #1: Enable sparse file support on Windows (saves disk space and I/O)
                    EnableSparseFile(xisoFs);

                    // Improvement #2: Buffer size optimization based on architecture
                    var bufferSize = Environment.Is64BitProcess
                        ? 1024 * 1024 // 1MB for 64-bit (better for SSDs)
                        : 128 * 1024; // 128KB for 32-bit
                    var buffer = new byte[bufferSize];

                    isoFs.Seek(inputOffset, SeekOrigin.Begin);
                    long numBytesProcessed = 0;

                    // Improvement #6: Fast path for already-contiguous files
                    // If there's only one contiguous range from header to last file, use single copy
                    if (validRanges.Count == 1 && validRanges[0].Start == headerOffsetSector)
                    {
                        _logger.LogMessage("[INFO] File has contiguous layout - using fast copy...");
                        var bytesToCopy = (lastValidSector + 1) * Utils.SectorSize;
                        var remaining = bytesToCopy;
                        while (remaining > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            var toRead = (int)Math.Min(buffer.Length, remaining);
                            var read = await isoFs.ReadAsync(buffer.AsMemory(0, toRead), token);
                            if (read == 0) break;

                            await xisoFs.WriteAsync(buffer.AsMemory(0, read), token);
                            remaining -= read;
                            numBytesProcessed += read;
                        }
                    }
                    else
                    {
                        // Standard sector-by-sector trimming loop
                        var progressTimer = Stopwatch.StartNew(); // Improvement #3

                        while (numBytesProcessed < targetXisoLength)
                        {
                            token.ThrowIfCancellationRequested();

                            var currentPhysicalByte = inputOffset + numBytesProcessed;
                            // Calculate sector index relative to start of disc
                            var currentSector = currentPhysicalByte / Utils.SectorSize;

                            // Trim everything after the last valid file extent
                            if (validRanges.Count > 0 && currentSector > lastValidSector)
                            {
                                break;
                            }

                            long bytesUntilEndOfExtent = 0;
                            long bytesToWipe = 0;

                            // Determine if current sector is data or filler
                            for (var i = 0; i < validRanges.Count; i++)
                            {
                                if (currentSector >= validRanges[i].Start && currentSector <= validRanges[i].End)
                                {
                                    bytesUntilEndOfExtent = (validRanges[i].End + 1) * Utils.SectorSize - currentPhysicalByte;
                                    break;
                                }
                                else if (currentSector < validRanges[i].Start && (i == 0 || currentSector > validRanges[i - 1].End))
                                {
                                    bytesToWipe = validRanges[i].Start * Utils.SectorSize - currentPhysicalByte;
                                    break;
                                }
                            }

                            if (bytesToWipe > 0)
                            {
                                if (bytesToWipe % Utils.SectorSize != 0)
                                {
                                    _logger.LogMessage("[ERROR] Unexpected Error: Filler data is not sector aligned.");
                                    return FileProcessingStatus.Failed;
                                }

                                // Wipe logic: Write zeroes to the output XISO
                                Utils.WriteZeroes(xisoFs, -1, bytesToWipe, buffer);
                                numBytesProcessed += bytesToWipe;

                                // [FIX] Moves the input stream forward when wiping.
                                isoFs.Seek(bytesToWipe, SeekOrigin.Current);
                            }
                            else
                            {
                                // Data logic: Copy valid sectors
                                var bytesToRead = bytesUntilEndOfExtent > 0 ? bytesUntilEndOfExtent : targetXisoLength - numBytesProcessed;

                                if (!Utils.FillBuffer(isoFs, xisoFs, -1, bytesToRead, buffer))
                                {
                                    _logger.LogMessage("[ERROR] Failed writing game partition data.");
                                    return FileProcessingStatus.Failed;
                                }

                                numBytesProcessed += bytesToRead;
                            }

                            // Improvement #3: Time-based progress reporting (every 500ms max)
                            if (progressTimer.ElapsedMilliseconds > 500)
                            {
                                progress.Report(new BatchOperationProgress { StatusText = $"Processing: {numBytesProcessed / (1024 * 1024)} MB" });
                                progressTimer.Restart();
                            }
                        }
                    } // End of else block for standard trimming

                    // Finalize file size (Trimming)
                    xisoFs.SetLength(xisoFs.Position);

                    // Improvement #5: Validate output size matches expectations
                    var expectedOutputSize = (lastValidSector + 1) * Utils.SectorSize;
                    if (xisoFs.Length != expectedOutputSize)
                    {
                        _logger.LogMessage($"[WARNING] Output size mismatch. Expected: {expectedOutputSize / (1024 * 1024)} MB, Got: {xisoFs.Length / (1024 * 1024)} MB");
                    }

                    _logger.LogMessage($"Successfully created trimmed XISO. Final size: {xisoFs.Length / (1024 * 1024)} MB");
                }

                // Move integrity check INSIDE the try block
                if (checkIntegrity)
                {
                    _logger.LogMessage("Verifying output XISO integrity...");
                    var isValid = await _integrityService.TestIsoIntegrityAsync(destPath, false, progress, token);

                    if (!isValid)
                    {
                        _logger.LogMessage("[ERROR] Rewritten XISO failed structural validation! Deleting corrupt output.");
                        try
                        {
                            if (File.Exists(destPath)) File.Delete(destPath);
                        }
                        catch
                        {
                            /* ignore */
                        }

                        return FileProcessingStatus.Failed;
                    }

                    _logger.LogMessage("Output XISO passed structural validation.");
                }

                return FileProcessingStatus.Converted;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Rewrite failed: {ex.Message}");
                return FileProcessingStatus.Failed;
            }
        }, token);
    }
}