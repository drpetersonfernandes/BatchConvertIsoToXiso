using System.Text.Json.Serialization;

namespace BatchConvertIsoToXiso.Models;

public class GitHubReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}