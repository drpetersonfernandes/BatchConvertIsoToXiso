namespace BatchConvertIsoToXiso.interfaces;

public interface IUpdateChecker
{
    Task<(bool IsNewVersionAvailable, string? LatestVersion, string? DownloadUrl)> CheckForUpdateAsync();
}