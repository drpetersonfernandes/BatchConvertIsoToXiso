namespace BatchConvertIsoToXiso.interfaces;

public interface IFileExtractor
{
    Task<bool> ExtractArchiveAsync(string archivePath, string extractionPath, CancellationToken token);
}