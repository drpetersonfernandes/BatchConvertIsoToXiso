namespace BatchConvertIsoToXiso.interfaces;

public interface IFileExtractor
{
    Task<bool> ExtractArchiveAsync(string archivePath, string extractionPath, CancellationTokenSource cts);
    Task<long> GetUncompressedArchiveSizeAsync(string archivePath, CancellationToken token);
}