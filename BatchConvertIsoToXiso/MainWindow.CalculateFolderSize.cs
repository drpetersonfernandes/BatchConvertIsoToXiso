using System.IO;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    /// <summary>
    /// Calculates the maximum temporary disk space required for a single item (ISO, CUE/BIN, or archive)
    /// during either a conversion or test operation, including a buffer.
    /// </summary>
    /// <param name="filePaths">List of file paths to consider.</param>
    /// <param name="isConversionOperation">True if calculating for conversion, false for testing.</param>
    /// <returns>The estimated maximum required temporary space in bytes.</returns>
    private async Task<long> CalculateMaxTempSpaceForSingleOperation(List<string> filePaths, bool isConversionOperation)
    {
        long maxEstimatedTempSpace = 0;

        foreach (var filePath in filePaths)
        {
            _cts.Token.ThrowIfCancellationRequested();
            try
            {
                var fileExtension = Path.GetExtension(filePath).ToLowerInvariant();
                long currentItemBaseSize = 0; // The base size of the item itself or its uncompressed form

                if (fileExtension == ".iso")
                {
                    currentItemBaseSize = await Task.Run(() => new FileInfo(filePath).Length);
                }
                else if (isConversionOperation) // Only conversion handles archives and CUE/BIN
                {
                    switch (fileExtension)
                    {
                        case ".cue":
                        {
                            var binPath = await ParseCueForBinFileAsync(filePath);
                            if (!string.IsNullOrEmpty(binPath))
                            {
                                currentItemBaseSize = await Task.Run(() => new FileInfo(binPath).Length);
                            }

                            break;
                        }
                        case ".zip" or ".7z" or ".rar":
                        {
                            try
                            {
                                currentItemBaseSize = await _fileExtractor.GetUncompressedArchiveSizeAsync(filePath, _cts.Token);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogMessage($"Warning: Could not get uncompressed size of archive {Path.GetFileName(filePath)} for disk space calculation: {ex.Message}. Using heuristic.");
                                currentItemBaseSize = new FileInfo(filePath).Length * 3; // Fallback heuristic: 3x compressed size
                            }

                            break;
                        }
                    }
                }
                else // Testing operation (only .iso files are passed for testing)
                {
                    currentItemBaseSize = await Task.Run(() => new FileInfo(filePath).Length);
                }

                // Now apply the specific multipliers based on operation and file type
                long currentItemPeakTempUsage = 0;
                if (isConversionOperation)
                {
                    if (fileExtension == ".iso")
                    {
                        // Standalone ISO: moved to temp, processed in-place.
                        // Needs space for the ISO + buffer for extract-xiso's internal temps.
                        currentItemPeakTempUsage = (long)(currentItemBaseSize * 1.5); // Existing 50% buffer
                    }
                    else if (fileExtension is ".cue" or ".zip" or ".7z" or ".rar")
                    {
                        // CUE/BIN: generated ISO (bin_size) + copy for processing (bin_size)
                        // Archive: extracted contents (uncompressed_size) + copy of largest internal ISO (assume uncompressed_size for safety)
                        // In both cases, roughly 2x the base size + a buffer for extract-xiso's internal temps.
                        currentItemPeakTempUsage = (long)(currentItemBaseSize * 2.0 * 1.2); // 2x base size + 20% buffer
                    }
                }
                else // Testing operation (only .iso files)
                {
                    // ISO is copied to temp, then extracted to a subfolder within temp.
                    // Needs space for copied ISO + space for extracted contents (assume same size) + buffer.
                    currentItemPeakTempUsage = (long)(currentItemBaseSize * 2.1); // Existing 110% buffer on 2x size
                }

                maxEstimatedTempSpace = Math.Max(maxEstimatedTempSpace, currentItemPeakTempUsage);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Warning: Could not get size of file {Path.GetFileName(filePath)} for disk space calculation: {ex.Message}");
                // Fallback to a very generous estimate (e.g., 100GB) to prevent proceeding with insufficient space.
                // This is a critical path, so better to over-estimate than under-estimate.
                maxEstimatedTempSpace = Math.Max(maxEstimatedTempSpace, 100L * 1024 * 1024 * 1024); // 100 GB fallback
            }
        }

        return maxEstimatedTempSpace;
    }

    private async Task<long> CalculateTotalInputFileSizeAsync(List<string> filePaths)
    {
        long totalSize = 0;
        foreach (var filePath in filePaths)
        {
            _cts.Token.ThrowIfCancellationRequested();
            try
            {
                totalSize += await Task.Run(() => new FileInfo(filePath).Length);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Warning: Could not get size of file {Path.GetFileName(filePath)} for disk space calculation: {ex.Message}");
                // Continue, but the totalSize might be underestimated.
            }
        }

        return totalSize;
    }
}