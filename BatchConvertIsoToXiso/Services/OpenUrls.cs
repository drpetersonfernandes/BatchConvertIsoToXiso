using System.Diagnostics;
using System.Windows;

namespace BatchConvertIsoToXiso.Services;

public interface IMessageBoxService
{
    void Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon);
    void ShowError(string message);
}

public class MessageBoxService : IMessageBoxService
{
    public void Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        if (Application.Current.MainWindow != null) MessageBox.Show(Application.Current.MainWindow, message, title, buttons, icon);
    }

    public void ShowError(string message)
    {
        Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

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
