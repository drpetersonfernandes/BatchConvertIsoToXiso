using System.IO;
using BatchConvertIsoToXiso.Models;

namespace BatchConvertIsoToXiso.Services.Xiso;

public class XisoWriter
{
    private readonly ILogger _logger;

    // XISO Types:                              XGD1,    XGD2,   XGD2-Hybrid,    XGD3
    private static readonly long[] XisoOffset = [0x18300000, 0xFD90000, 0x89D80000, 0x2080000];
    private static readonly long[] XisoLength = [0x1A2DB0000, 0x1B3880000, 0xBF8A0000, 0x204510000];

    // Redump ISO Types:                               XGD1,      XGD2w0,      XGD2w1,      XGD2w2,     XGD2w3+, XGD2-Hybrid,      XGD3v0,     XGD3
    private static readonly long[] RedumpIsoLength = [0x1D26A8000, 0x1D3301800, 0x1D2FEF800, 0x1D3082000, 0x1D3390000, 0x1D31A0000, 0x208E05800, 0x208E03800];

    public XisoWriter(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> RewriteIsoAsync(string sourcePath, string destPath, bool skipSystemUpdate, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            try
            {
                FileInfo isoInfo = new(sourcePath);
                var isoSize = isoInfo.Length;
                var redumpIsoType = Array.IndexOf(RedumpIsoLength, isoSize);
                var xisoType = Array.IndexOf(XisoLength, isoSize);

                if (redumpIsoType < 0 && xisoType < 0)
                {
                    _logger.LogMessage($"[ERROR] Unknown ISO type. Size: {isoSize}");
                    return false;
                }

                long inputOffset;
                long targetLength;

                if (redumpIsoType >= 0)
                {
                    // Mode 1: Redump ISO as input
                    long xgdType = redumpIsoType switch
                    {
                        0 => 0, // XGD1
                        1 or 2 or 3 or 4 => 1, // XGD2
                        5 => 2, // XGD2 (Hybrid)
                        6 or 7 => 3, // XGD3
                        _ => 0
                    };
                    inputOffset = XisoOffset[xgdType];
                    targetLength = XisoLength[xgdType];
                    _logger.LogMessage($"Detected Redump ISO (Type {xgdType}). Extracting XISO partition...");
                }
                else
                {
                    // Mode 3: XISO as input
                    inputOffset = 0;
                    targetLength = isoSize;
                    _logger.LogMessage("Detected XISO. Optimizing/Wiping...");
                }

                using FileStream isoFs = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using FileStream xisoFs = new(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

                // Parse XISO filesystem for all file extents
                // Note: XboxKit logic does not support filtering by filename easily, so skipSystemUpdate is ignored to ensure stability.
                if (skipSystemUpdate)
                {
                    _logger.LogMessage("[INFO] 'Skip System Update' is not supported with the current engine. Performing full valid rewrite.");
                }

                var validRanges = Xdvdfs.GetXisoRanges(isoFs, inputOffset, true);

                isoFs.Seek(inputOffset, SeekOrigin.Begin);
                long numBytes = 0;

                // We enable "wipe" behavior (writing zeroes to filler) by default for optimization

                while (numBytes < targetLength)
                {
                    token.ThrowIfCancellationRequested();

                    var currentByte = inputOffset + numBytes;
                    var currentSector = (currentByte + Utils.SectorSize - 1) / Utils.SectorSize;
                    long bytesUntilEndOfExtent = 0;
                    long bytesToWipe = 0;

                    // Determine whether current sector is after last file extent
                    if (validRanges.Count > 0 && currentSector > validRanges[^1].End)
                    {
                        // Remainder of XISO is filler
                        bytesToWipe = targetLength - numBytes;
                    }
                    else
                    {
                        // Determine whether current sector is within a file extent or filler data
                        for (var i = 0; i < validRanges.Count; i++)
                        {
                            if (currentSector >= validRanges[i].Start && currentSector <= validRanges[i].End)
                            {
                                // Number of bytes remaining in current file extent
                                bytesUntilEndOfExtent = (validRanges[i].End + 1) * Utils.SectorSize - currentByte;
                                break;
                            }
                            else if (currentSector < validRanges[i].Start && (i == 0 || currentSector > validRanges[i - 1].End))
                            {
                                // Wipe until next file extent
                                bytesToWipe = validRanges[i].Start * Utils.SectorSize - currentByte;
                                break;
                            }
                        }
                    }

                    if (bytesToWipe > 0)
                    {
                        // Write zeroes to XISO
                        Utils.WriteZeroes(xisoFs, -1, bytesToWipe);
                        numBytes += bytesToWipe;

                        // Move ahead in ISO file
                        isoFs.Seek(bytesToWipe, SeekOrigin.Current);
                    }
                    else
                    {
                        // Write data to XISO
                        long bytesToRead;
                        if (bytesToWipe > 0)
                        {
                            bytesToRead = bytesToWipe;
                        }
                        else if (bytesUntilEndOfExtent > 0)
                        {
                            bytesToRead = bytesUntilEndOfExtent;
                        }
                        else
                        {
                            bytesToRead = targetLength - numBytes;
                        }

                        if (!Utils.WriteBytes(isoFs, xisoFs, -1, bytesToRead))
                        {
                            _logger.LogMessage("[ERROR] Failed writing game partition (XISO).");
                            return false;
                        }

                        numBytes += bytesToRead;
                    }

                    // Report progress occasionally
                    if (numBytes % (10 * 1024 * 1024) == 0) // Every 10MB
                    {
                        progress.Report(new BatchOperationProgress { StatusText = $"Writing: {numBytes / (1024 * 1024)} MB / {targetLength / (1024 * 1024)} MB" });
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Rewrite failed: {ex.Message}");
                return false;
            }
        }, token);
    }
}
