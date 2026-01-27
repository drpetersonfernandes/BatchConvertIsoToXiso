using System.Diagnostics;
using BatchConvertIsoToXiso.interfaces;

namespace BatchConvertIsoToXiso.Services;

public class DiskMonitorService : IDiskMonitorService, IDisposable
{
    private readonly ILogger _logger;
    private PerformanceCounter? _diskWriteSpeedCounter;

    public string? CurrentDriveLetter { get; private set; }

    public DiskMonitorService(ILogger logger)
    {
        _logger = logger;
    }

    public void StartMonitoring(string? path)
    {
        var driveLetter = PathHelper.GetDriveLetter(path);
        if (CurrentDriveLetter == driveLetter) return;

        StopMonitoring();

        if (string.IsNullOrEmpty(driveLetter))
        {
            return;
        }

        var perfCounterInstanceName = driveLetter.EndsWith(':') ? driveLetter : driveLetter + ":";

        try
        {
            if (!PerformanceCounterCategory.Exists("LogicalDisk") ||
                !PerformanceCounterCategory.InstanceExists(perfCounterInstanceName, "LogicalDisk"))
            {
                _logger.LogMessage($"Performance counter for drive {perfCounterInstanceName} not available.");
                return;
            }

            var counter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", perfCounterInstanceName, true);
            counter.NextValue(); // Prime the counter
            _diskWriteSpeedCounter = counter;
            CurrentDriveLetter = driveLetter;
            _logger.LogMessage($"Monitoring write speed for drive: {perfCounterInstanceName}");
        }
        catch (Exception ex)
        {
            _logger.LogMessage($"Failed to initialize disk monitor for {perfCounterInstanceName}: {ex.Message}");
            StopMonitoring();
        }
    }

    public void StopMonitoring()
    {
        _diskWriteSpeedCounter?.Dispose();
        _diskWriteSpeedCounter = null;
        CurrentDriveLetter = null;
    }

    public string GetCurrentWriteSpeedFormatted()
    {
        if (_diskWriteSpeedCounter == null) return "N/A";

        try
        {
            var val = _diskWriteSpeedCounter.NextValue();
            return Formatter.FormatBytesPerSecond(val);
        }
        catch
        {
            StopMonitoring();
            return "N/A";
        }
    }

    public void Dispose()
    {
        StopMonitoring();
        GC.SuppressFinalize(this);
    }
}