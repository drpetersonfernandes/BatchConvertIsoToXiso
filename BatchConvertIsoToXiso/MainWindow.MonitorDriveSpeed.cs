using System.Diagnostics;
using System.IO;
using System.Windows;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private string? GetDriveLetter(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            // Ensure the path is absolute to get a root.
            var fullPath = Path.GetFullPath(path);
            var pathRoot = Path.GetPathRoot(fullPath);

            if (string.IsNullOrEmpty(pathRoot)) return null;

            var driveInfo = new DriveInfo(pathRoot);
            // driveInfo.Name will be like "C:\\" for local drives.
            // We need "C:" for the performance counter instance name.
            return driveInfo.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException) // Handles invalid paths, UNC paths for which DriveInfo might fail
        {
            _logger.LogMessage($"Could not determine drive letter for path: {path}. It might be a network path or invalid.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Error getting drive letter for path {path}: {ex.Message}");
            return null;
        }
    }

    private void SetCurrentOperationDrive(string? driveLetter)
    {
        if (_currentOperationDrive == driveLetter) return;

        _currentOperationDrive = driveLetter;

        // Switch performance counter to the new drive
        if (!string.IsNullOrEmpty(driveLetter) && _processingTimer.IsEnabled)
        {
            InitializePerformanceCounter(driveLetter);
        }
    }

    private void InitializePerformanceCounter(string? driveLetter)
    {
        StopPerformanceCounter(); // Stop any existing counter

        if (string.IsNullOrEmpty(driveLetter))
        {
            _logger.LogMessage("Cannot monitor write speed: Drive letter is invalid or not determined (e.g., network path).");
            Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A");
            return;
        }

        var perfCounterInstanceName = driveLetter.EndsWith(':') ? driveLetter : driveLetter + ":";

        try
        {
            // First, check if the category exists. If not, we can't proceed.
            if (!PerformanceCounterCategory.Exists("LogicalDisk"))
            {
                _logger.LogMessage($"Performance counter category 'LogicalDisk' does not exist. Cannot monitor write speed for drive {perfCounterInstanceName}.");
                Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A (Category Missing)");
                return;
            }

            // Now, check if the specific instance exists for the given drive letter.
            if (!PerformanceCounterCategory.InstanceExists(perfCounterInstanceName, "LogicalDisk"))
            {
                _logger.LogMessage($"Performance counter instance '{perfCounterInstanceName}' not found for 'LogicalDisk'. Cannot monitor write speed for this drive.");
                Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "N/A (Instance Missing)");
                return;
            }

            // If both category and instance exist, proceed with creating the counter.
            _diskWriteSpeedCounter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", perfCounterInstanceName, true);
            _diskWriteSpeedCounter.NextValue(); // Initial call to prime the counter, ignore the first value
            // Second call to get a valid initial value, though it might still be 0
            _diskWriteSpeedCounter.NextValue();
            _activeMonitoringDriveLetter = driveLetter;
            _logger.LogMessage($"Monitoring write speed for drive: {perfCounterInstanceName}");
            Application.Current.Dispatcher.Invoke(() => WriteSpeedValue.Text = "Calculating...");
        }
        catch (InvalidOperationException ex)
        {
            // This catch block should now primarily handle issues during counter-creation/access after existence checks.
            _logger.LogMessage($"Error initializing performance counter for drive {perfCounterInstanceName}: {ex.Message}. Write speed monitoring disabled.");
            _ = ReportBugAsync($"PerfCounter Init InvalidOpExc for {perfCounterInstanceName}", ex);
            _diskWriteSpeedCounter?.Dispose();
            _diskWriteSpeedCounter = null;
            _activeMonitoringDriveLetter = null;
            WriteSpeedValue.Text = "N/A (Error)";
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Unexpected error initializing performance counter for drive {perfCounterInstanceName}: {ex.Message}. Write speed monitoring disabled.");
            _ = ReportBugAsync($"PerfCounter Init GenericExc for {perfCounterInstanceName}", ex);
            _diskWriteSpeedCounter?.Dispose();
            _diskWriteSpeedCounter = null;
            _activeMonitoringDriveLetter = null;
            WriteSpeedValue.Text = "N/A (Error)";
        }
        finally
        {
            WriteSpeedDriveIndicator.Text = _activeMonitoringDriveLetter != null ? $"({_activeMonitoringDriveLetter})" : "";
        }
    }

    private void StopPerformanceCounter()
    {
        _diskWriteSpeedCounter?.Dispose();
        _diskWriteSpeedCounter = null;
        _activeMonitoringDriveLetter = null;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (WriteSpeedValue != null)
            {
                WriteSpeedValue.Text = "N/A";
            }

            if (WriteSpeedDriveIndicator != null)
            {
                WriteSpeedDriveIndicator.Text = "";
            }
        });
    }
}