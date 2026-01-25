namespace BatchConvertIsoToXiso.Models;

public class BatchOperationProgress
{
    public string? LogMessage { get; set; }
    public string? StatusText { get; set; }
    public int? TotalFiles { get; set; }
    public int? ProcessedCount { get; set; }
    public int? SuccessCount { get; set; }
    public int? FailedCount { get; set; }
    public int? SkippedCount { get; set; }
    public string? CurrentDrive { get; set; }
    public string? FailedPathToAdd { get; set; }
    public bool IsIndeterminate { get; set; }
}