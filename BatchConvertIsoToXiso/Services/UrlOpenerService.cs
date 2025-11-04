using System.Diagnostics;

namespace BatchConvertIsoToXiso.Services;

public interface IUrlOpener
{
    void OpenUrl(string url);
}

public class UrlOpenerService : IUrlOpener
{
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly IMessageBoxService _messageBoxService;

    public UrlOpenerService(ILogger logger, IBugReportService bugReportService, IMessageBoxService messageBoxService)
    {
        _logger = logger;
        _bugReportService = bugReportService;
        _messageBoxService = messageBoxService;
    }

    public void OpenUrl(string url)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            process?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error opening URL: {url}. Exception: {ex.Message}");
            _ = _bugReportService.SendBugReportAsync($"Error opening URL: {url}. Exception: {ex}");
            _messageBoxService.ShowError($"Unable to open link: {ex.Message}");
        }
    }
}