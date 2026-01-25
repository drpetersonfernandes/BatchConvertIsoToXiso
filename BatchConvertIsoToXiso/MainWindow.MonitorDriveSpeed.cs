using System.IO;
using System.Windows;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private static string? GetDriveLetter(string? path)
    {
        // You can keep this helper if other parts of MainWindow use it,
        // but the monitoring logic now uses the service's internal version.
        // For now, we'll keep it to avoid breaking BatchTest.cs
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            return Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private void SetCurrentOperationDrive(string? driveLetter)
    {
        // Simply delegate to the service
        _diskMonitorService.StartMonitoring(driveLetter);
    }

    private void StopPerformanceCounter()
    {
        _diskMonitorService.StopMonitoring();
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            WriteSpeedValue?.Text = "N/A";
            WriteSpeedDriveIndicator?.Text = "";
        });
    }
}