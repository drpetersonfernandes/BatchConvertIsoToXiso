using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BatchConvertIsoToXiso.Models;

namespace BatchConvertIsoToXiso.Services;

public interface IUpdateChecker
{
    Task<(bool IsNewVersionAvailable, string? LatestVersion, string? DownloadUrl)> CheckForUpdateAsync();
}

public partial class UpdateChecker : IUpdateChecker, IDisposable
{
    private const string GitHubApiUrl = "https://api.github.com/repos/drpetersonfernandes/BatchConvertIsoToXiso/releases/latest";
    private readonly HttpClient _httpClient;

    public UpdateChecker()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        // GitHub API requires a User-Agent header.
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BatchConvertIsoToXiso", GetApplicationVersion.GetProgramVersion()));
    }

    public async Task<(bool IsNewVersionAvailable, string? LatestVersion, string? DownloadUrl)> CheckForUpdateAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync(GitHubApiUrl);
            var releaseInfo = JsonSerializer.Deserialize<GitHubReleaseInfo>(response);

            if (releaseInfo?.TagName is null || releaseInfo.HtmlUrl is null)
            {
                return (false, null, null);
            }

            var versionMatch = MyRegex().Match(releaseInfo.TagName);
            if (!versionMatch.Success)
            {
                // If no version number can be extracted, treat as no update available.
                return (false, null, null);
            }

            var latestVersionStr = versionMatch.Value;

            if (Version.TryParse(latestVersionStr, out var latestVersion))
            {
                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (currentVersion != null && latestVersion > currentVersion)
                {
                    return (true, latestVersion.ToString(), releaseInfo.HtmlUrl);
                }
            }
        }
        catch (Exception)
        {
            // Silently fail on any error (e.g., no internet connection, API change)
            return (false, null, null);
        }

        return (false, null, null);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"\d+(\.\d+){1,3}")]
    private static partial Regex MyRegex();
}