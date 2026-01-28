using System.Windows.Controls;

namespace BatchConvertIsoToXiso.interfaces;

public interface ILogger
{
    void Initialize(TextBox logViewer);
    void LogMessage(string message);
}