namespace BatchConvertIsoToXiso.Services;

public interface IDiskMonitorService
{
    string? CurrentDriveLetter { get; }
    void StartMonitoring(string? path);
    void StopMonitoring();
    string GetCurrentWriteSpeedFormatted();
}