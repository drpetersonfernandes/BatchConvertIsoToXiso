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
        long maxRequiredSpace = 0;
        foreach (var filePath in filePaths)
        {
            _cts.Token.ThrowIfCancellationRequested();
            try
            {
                var fileExtension = Path.GetExtension(filePath).ToLowerInvariant();
                long currentItemSize = 0;

                if (fileExtension == ".iso")
                {
                    currentItemSize = await Task.Run(() => new FileInfo(filePath).Length);
                }
                else
                    switch (isConversionOperation)
                    {
                        case true when fileExtension == ".cue":
                        {
                            // For CUE, we need space for the resulting ISO. Assume it's roughly the size of the largest BIN.
                            var binPath = await ParseCueForBinFileAsync(filePath);
                            if (!string.IsNullOrEmpty(binPath)) // ParseCueForBinFileAsync already checks File.Exists
                            {
                                currentItemSize = await Task.Run(() => new FileInfo(binPath).Length);
                            }

                            break;
                        }
                        case true when (fileExtension == ".zip" || fileExtension == ".7z" || fileExtension == ".rar"):
                            // For archives, we need space for the extracted contents.
                            try
                            {
                                currentItemSize = await _fileExtractor.GetUncompressedArchiveSizeAsync(filePath, _cts.Token);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogMessage($"Warning: Could not get uncompressed size of archive {Path.GetFileName(filePath)} for disk space calculation: {ex.Message}. Using heuristic.");
                                currentItemSize = new FileInfo(filePath).Length * 3; // Fallback heuristic: 3x compressed size
                            }

                            break;
                    }

                maxRequiredSpace = Math.Max(maxRequiredSpace, currentItemSize);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogMessage($"Warning: Could not get size of file {Path.GetFileName(filePath)} for disk space calculation: {ex.Message}");
            }
        }

        return isConversionOperation ? (long)(maxRequiredSpace * 1.5) : (long)(maxRequiredSpace * 2.1);
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