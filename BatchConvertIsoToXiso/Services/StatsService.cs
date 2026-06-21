using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BatchConvertIsoToXiso.Interfaces;

namespace BatchConvertIsoToXiso.Services;

public class StatsService : IStatsService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _applicationId;

    public StatsService(HttpClient httpClient, string apiUrl, string apiKey, string applicationId)
    {
        _apiUrl = apiUrl;
        _applicationId = applicationId;
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task SendStatsAsync()
    {
        try
        {
            var version = GetApplicationVersion.GetProgramVersion();
            var payload = new { applicationId = _applicationId, version };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(_apiUrl, content);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // Silently ignore network or other errors during startup stats reporting
        }
    }
}