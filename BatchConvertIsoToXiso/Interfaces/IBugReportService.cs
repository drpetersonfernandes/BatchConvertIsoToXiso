namespace BatchConvertIsoToXiso.Interfaces;

public interface IBugReportService
{
    Task<bool> SendBugReportAsync(string message);
    Task<bool> SendBugReportAsync(string errorMessage, Exception exception);
}