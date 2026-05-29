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

    public Task<bool> SendBugReportAsync(string message)
    {
        var fullMessage = BuildFullMessage(message);
        var version = GetApplicationVersion.GetProgramVersion();
        return SendToApiAsync(fullMessage, version);
    }

    public Task<bool> SendBugReportAsync(string errorMessage, Exception exception)
    {
        var fullMessage = BuildFullMessage(errorMessage);
        var version = GetApplicationVersion.GetProgramVersion();
        return SendToApiAsync(fullMessage, version);
    }

    private async Task<bool> SendToApiAsync(string fullMessage, string version)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                { "message", fullMessage },
                { "applicationName", _applicationName },
                { "version", version }
            };

            var content = JsonContent.Create(payload);

            var response = await _httpClient.PostAsync(_apiUrl, content);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    internal static string BuildFullMessage(string message)
    {
        if (message.Contains("=== Environment Details ===", StringComparison.OrdinalIgnoreCase))
        {
            return message;
        }

        var sb = new StringBuilder();
        AppendEnvironmentDetails(sb);
        sb.AppendLine();
        sb.AppendLine(message);

        return sb.ToString();
    }

    internal static void AppendEnvironmentDetails(StringBuilder sb)
    {
        sb.AppendLine("=== Environment Details ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Name: {App.ApplicationName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Version: {GetApplicationVersion.GetProgramVersion()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS Version: {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Bitness: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Windows Version: {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{Environment.OSVersion.Version.Build}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Processor Count: {Environment.ProcessorCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Base Directory: {AppContext.BaseDirectory}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Temp Path: {Path.GetTempPath()}");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
