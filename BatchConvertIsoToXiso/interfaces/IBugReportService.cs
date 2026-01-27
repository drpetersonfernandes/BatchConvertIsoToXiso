namespace BatchConvertIsoToXiso.interfaces;

public interface IBugReportService
{
    Task<bool> SendBugReportAsync(string message);
}