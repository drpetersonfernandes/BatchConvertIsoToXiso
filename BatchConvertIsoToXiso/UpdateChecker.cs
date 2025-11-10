using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BatchConvertIsoToXiso.Models;

namespace BatchConvertIsoToXiso;

public interface IUpdateChecker
{
    Task<(bool IsNewVersionAvailable, string? LatestVersion, string? DownloadUrl)> CheckForUpdateAsync();
}

public class UpdateChecker : IUpdateChecker, IDisposable
{
    private const string GitHubApiUrl = "https://api.github.com/repos/drpetersonfernandes/BatchConvertIsoToXiso/releases/latest";
    private readonly HttpClient _httpClient;

    public UpdateChecker()
    {
        _httpClient = new HttpClient();
        // GitHub API requires a User-Agent header.
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BatchConvertIsoToXiso", GetApplicationVersion()));
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

            // Use a regular expression to extract a semantic version number (e.g., X.Y.Z)
            // This makes the parsing more robust against varying tag prefixes like "release-" or "v".
            var versionMatch = Regex.Match(releaseInfo.TagName, @"\d+\.\d+\.\d+(\.\d+)?");
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

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "1.0.0";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}