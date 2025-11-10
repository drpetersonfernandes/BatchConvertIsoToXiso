using System.Net.Http;
using System.Net.Http.Json;

namespace BatchConvertIsoToXiso;

public interface IBugReportService
{
    Task<bool> SendBugReportAsync(string message);
}

public class BugReportService : IBugReportService, IDisposable
{
    private readonly HttpClient _httpClient = new();
    private bool _disposed;
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _applicationName;

    public BugReportService(string apiUrl, string apiKey, string applicationName)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _applicationName = applicationName;

        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.Add("X-API-KEY", _apiKey); // Set API key once
    }

    /// <summary>
    /// Silently sends a bug report to the API
    /// </summary>
    /// <param name="message">The error message or bug report</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public async Task<bool> SendBugReportAsync(string message)
    {
        try
        {
            // Create the request payload
            var content = JsonContent.Create(new
            {
                message,
                applicationName = _applicationName
            });

            // Send the request
            var response = await _httpClient.PostAsync(_apiUrl, content);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Silently fail if there's an exception
            return false;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _httpClient.Dispose();
        }

        _disposed = true;
    }
}