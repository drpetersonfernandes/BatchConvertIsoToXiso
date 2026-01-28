namespace BatchConvertIsoToXiso.interfaces;

public interface IFileExtractor
{
    Task<bool> ExtractArchiveAsync(string archivePath, string extractionPath, CancellationToken token);
    Task<long> GetUncompressedArchiveSizeAsync(string archivePath, CancellationToken token);
}