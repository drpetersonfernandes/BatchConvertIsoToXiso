using System.IO;
using BatchConvertIsoToXiso.interfaces;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;
using BatchConvertIsoToXiso.Services.XisoServices.XDVDFS;

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
                    // If it's not a Redump size, treat it as an XISO (Standard or already trimmed)
                    inputOffset = 0;
                    targetXisoLength = isoSize;
                }

                await using FileStream isoFs = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                // Generate valid ranges based on XDVDFS traversal
                List<(uint Start, uint End)> validRanges;
                try
                {
                    validRanges = Xdvdfs.GetXisoRanges(isoFs, inputOffset, true, skipSystemUpdate);
                }
                catch
                {
                    _logger.LogMessage($"[ERROR] '{Path.GetFileName(sourcePath)}' is not a valid Xbox ISO image.");
                    return FileProcessingStatus.Failed;
                }

                // If only the header sectors were found, it's not a valid Xbox ISO
                if (validRanges.Count <= 1)
                {
                    _logger.LogMessage($"[ERROR] '{Path.GetFileName(sourcePath)}' contains no valid Xbox filesystem.");
                    return FileProcessingStatus.Failed;
                }

                var lastValidSector = validRanges.Count > 0 ? validRanges[^1].End : 0;

                // Check if already optimized:
                // If it's an XISO (offset 0) and current size is <= calculated trimmed size
                var optimizedSize = (lastValidSector + 1) * Utils.SectorSize;
                if (inputOffset == 0 && isoSize <= optimizedSize)
                {
                    _logger.LogMessage($"[INFO] File '{Path.GetFileName(sourcePath)}' is already optimized. Skipping.");
                    return FileProcessingStatus.AlreadyOptimized;
                }

                _logger.LogMessage(inputOffset == 0 ? "Detected XISO. Trimming..." : "Detected Redump ISO. Extracting...");

                {
                    await using FileStream xisoFs = new(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

                    // Pre-allocate buffer to avoid GC pressure in the loop
                    var buffer = new byte[64 * Utils.SectorSize];

                    isoFs.Seek(inputOffset, SeekOrigin.Begin);
                    long numBytesProcessed = 0;

                    while (numBytesProcessed < targetXisoLength)
                    {
                        token.ThrowIfCancellationRequested();

                        var currentPhysicalByte = inputOffset + numBytesProcessed;
                        // Calculate sector index relative to start of disc
                        var currentSector = (currentPhysicalByte + Utils.SectorSize - 1) / Utils.SectorSize;

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

                            if (!Utils.WriteBytes(isoFs, xisoFs, -1, bytesToRead, buffer))
                            {
                                _logger.LogMessage("[ERROR] Failed writing game partition data.");
                                return FileProcessingStatus.Failed;
                            }

                            numBytesProcessed += bytesToRead;
                        }

                        // UI Progress Update
                        if (numBytesProcessed % (100 * 1024 * 1024) == 0)
                        {
                            progress.Report(new BatchOperationProgress { StatusText = $"Processing: {numBytesProcessed / (1024 * 1024)} MB" });
                        }
                    }

                    // Finalize file size (Trimming)
                    xisoFs.SetLength(xisoFs.Position);
                    _logger.LogMessage($"Successfully created trimmed XISO. Final size: {xisoFs.Length / (1024 * 1024)} MB");
                }

                // Move integrity check INSIDE the try block
                if (checkIntegrity)
                {
                    _logger.LogMessage("Verifying output XISO integrity...");
                    var isValid = await _integrityService.TestIsoIntegrityAsync(destPath, false, new Progress<BatchOperationProgress>(), token);

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