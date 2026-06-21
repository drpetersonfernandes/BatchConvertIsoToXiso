using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using BatchConvertIsoToXiso.Interfaces;
using BatchConvertIsoToXiso.Models;

namespace BatchConvertIsoToXiso.Services;

public partial class UpdateChecker : IUpdateChecker
{
    private const string GitHubApiUrl = "https://api.github.com/repos/drpetersonfernandes/BatchConvertIsoToXiso/releases/latest";
    private readonly HttpClient _httpClient;
    private readonly string _currentVersion;

    public UpdateChecker(HttpClient httpClient)
        : this(httpClient, GetApplicationVersion.GetProgramVersion())
    {
    }

    internal UpdateChecker(HttpClient httpClient, string currentVersion)
    {
        _httpClient = httpClient;
        _currentVersion = currentVersion;
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BatchConvertIsoToXiso", currentVersion));
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
                return (false, null, null);
            }

            var latestVersionStr = versionMatch.Value;

            if (Version.TryParse(latestVersionStr, out var latestVersion) &&
                Version.TryParse(_currentVersion, out var currentVersion) &&
                latestVersion > currentVersion)
            {
                return (true, latestVersion.ToString(), releaseInfo.HtmlUrl);
            }
        }
        catch (Exception)
        {
            return (false, null, null);
        }

        return (false, null, null);
    }

    [GeneratedRegex(@"\d+(\.\d+){1,3}")]
    private static partial Regex MyRegex();
}
