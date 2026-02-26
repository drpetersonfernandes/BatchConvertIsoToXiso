namespace BatchConvertIsoToXiso.interfaces;

public interface IDiskMonitorService
{
    string? CurrentDriveLetter { get; }
    string? StatusMessage { get; }
    void StartMonitoring(string? path);
    void StopMonitoring();
    string GetCurrentReadSpeedFormatted();
    string GetCurrentWriteSpeedFormatted();
    long GetAvailableFreeSpace(string? path);
}