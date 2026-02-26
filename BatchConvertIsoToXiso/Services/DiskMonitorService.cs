using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

    // P/Invoke for GetDiskFreeSpaceEx which works with UNC paths
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

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

    public long GetAvailableFreeSpace(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        try
        {
            // Handle UNC paths (network shares) using P/Invoke
            if (PathHelper.IsUncPath(path))
            {
                // For UNC paths, use GetDiskFreeSpaceEx which works with network shares
                // We need to pass the share root (\\server\share) not a subdirectory
                var shareInfo = PathHelper.TryGetUncShareInfo(path);
                if (shareInfo.HasValue)
                {
                    var shareRoot = $@"\\{shareInfo.Value.Server}\{shareInfo.Value.Share}";
                    if (GetDiskFreeSpaceEx(shareRoot, out var freeBytesAvailable, out _, out _))
                    {
                        return (long)freeBytesAvailable;
                    }
                }

                // Fallback: try the path as-is
                if (GetDiskFreeSpaceEx(path, out var freeBytes, out _, out _))
                {
                    return (long)freeBytes;
                }

                return 0;
            }

            // Handle mapped network drives and local drives
            var driveLetter = PathHelper.GetDriveLetter(path);
            if (!string.IsNullOrEmpty(driveLetter))
            {
                var driveInfo = new DriveInfo(driveLetter);
                if (driveInfo.IsReady)
                {
                    return driveInfo.AvailableFreeSpace;
                }
            }

            // Fallback for other cases
            var fallbackDriveInfo = new DriveInfo(path);
            if (fallbackDriveInfo.IsReady)
            {
                return fallbackDriveInfo.AvailableFreeSpace;
            }
        }
        catch
        {
            // Ignore errors and return 0
        }

        return 0;
    }

    public void Dispose()
    {
        StopMonitoring();
        GC.SuppressFinalize(this);
    }
}