using System.Windows;
using System.Windows.Controls;

namespace BatchConvertIsoToXiso.Services;

public interface ILogger
{
    void Initialize(TextBox logViewer);
    void LogMessage(string message);
}

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
        Application.Current.Dispatcher.Invoke(() =>
        {
            _logViewer.AppendText($"{timestampedMessage}{Environment.NewLine}");
            _logViewer.ScrollToEnd();
        });
    }
}