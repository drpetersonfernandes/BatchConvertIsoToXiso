namespace BatchConvertIsoToXiso.interfaces;

public interface IDiskMonitorService
{
    string? CurrentDriveLetter { get; }
    void StartMonitoring(string? path);
    void StopMonitoring();
    string GetCurrentWriteSpeedFormatted();
    long GetAvailableFreeSpace(string? path);
}