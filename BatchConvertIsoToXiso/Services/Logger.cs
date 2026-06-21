using System.Windows;
using System.Windows.Controls;
using BatchConvertIsoToXiso.Interfaces;

namespace BatchConvertIsoToXiso.Services;

public class LoggerService : ILogger
{
    private TextBox? _logViewer;
    private const int MaxLogLength = 100000; // Approx 1000-2000 lines depending on length

    public void Initialize(TextBox logViewer)
    {
        _logViewer = logViewer;
    }

    public void LogMessage(string message)
    {
        var logViewer = _logViewer;
        if (logViewer == null)
        {
            return;
        }

        var timestampedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        _ = dispatcher.InvokeAsync(() =>
        {
            // Truncate the log if it gets too long to prevent UI freezing
            if (logViewer.Text.Length > MaxLogLength)
            {
                var text = logViewer.Text;
                // Keep the last ~50% of the log, try to cut at a newline
                var cutIndex = text.IndexOf('\n', text.Length / 2);
                logViewer.Text = cutIndex >= 0 ? text.Substring(cutIndex + 1) : text.Substring(text.Length / 2);
            }

            logViewer.AppendText($"{timestampedMessage}{Environment.NewLine}");
            logViewer.ScrollToEnd();
        });
    }
}
