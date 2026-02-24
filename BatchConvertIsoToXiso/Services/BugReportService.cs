using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using BatchConvertIsoToXiso.interfaces;

namespace BatchConvertIsoToXiso.Services;

public class BugReportService : IBugReportService, IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _applicationName;

    public BugReportService(string apiUrl, string apiKey, string applicationName)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _applicationName = applicationName;

        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.Add("X-API-KEY", _apiKey);
    }

    public async Task<bool> SendBugReportAsync(string message)
    {
        try
        {
            var fullMessage = BuildFullMessage(message);

            // Create the request payload
            var content = JsonContent.Create(new
            {
                message = fullMessage,
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

    private static string BuildFullMessage(string message)
    {
        if (message.Contains("=== Environment Details ===", StringComparison.OrdinalIgnoreCase))
        {
            return message;
        }

        var sb = new StringBuilder(message);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("=== Environment Details ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS Version: {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Windows Version: {Environment.OSVersion.Version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $".NET Version: {RuntimeInformation.FrameworkDescription.Replace(".NET ", "", StringComparison.OrdinalIgnoreCase)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Process Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Processor Count: {Environment.ProcessorCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Version: {GetApplicationVersion.GetProgramVersion()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Base Directory: {AppDomain.CurrentDomain.BaseDirectory}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Temp Path: {Path.GetTempPath()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"User: {Environment.UserName}");

        return sb.ToString();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}