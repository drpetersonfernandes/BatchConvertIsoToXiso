using System.Windows;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
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