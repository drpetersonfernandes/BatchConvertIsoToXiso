namespace BatchConvertIsoToXiso.Interfaces;

public interface IUpdateChecker
{
    Task<(bool IsNewVersionAvailable, string? LatestVersion, string? DownloadUrl)> CheckForUpdateAsync();
}