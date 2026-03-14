using System.Windows;
using System.Windows.Controls;
using BatchConvertIsoToXiso.interfaces;

namespace BatchConvertIsoToXiso.Services;

public class LoggerService : ILogger
{
    private TextBox? _logViewer;

    public void Initialize(TextBox logViewer)
    {
        _logViewer = logViewer;
    }

    public void LogMessage(string message)
    {
        if (_logViewer == null)
        {
            return;
        }

        var timestampedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _logViewer.AppendText($"{timestampedMessage}{Environment.NewLine}");
            _logViewer.ScrollToEnd();
        });
    }
}