using BatchConvertIsoToXiso.Models;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Models;

public class BatchOperationProgressTests
{
    [Fact]
    public void DefaultValuesAreNullOrDefault()
    {
        var progress = new BatchOperationProgress();

        Assert.Null(progress.LogMessage);
        Assert.Null(progress.StatusText);
        Assert.Null(progress.TotalFiles);
        Assert.Null(progress.ProcessedCount);
        Assert.Null(progress.SuccessCount);
        Assert.Null(progress.FailedCount);
        Assert.Null(progress.SkippedCount);
        Assert.Null(progress.CurrentDrive);
        Assert.Null(progress.FailedPathToAdd);
    }

    [Fact]
    public void PropertiesCanBeSet()
    {
        var progress = new BatchOperationProgress
        {
            LogMessage = "Test log",
            StatusText = "Testing...",
            TotalFiles = 10,
            ProcessedCount = 5,
            SuccessCount = 4,
            FailedCount = 1,
            SkippedCount = 0,
            CurrentDrive = "C:",
            FailedPathToAdd = "C:\\failed.iso"
        };

        Assert.Equal("Test log", progress.LogMessage);
        Assert.Equal("Testing...", progress.StatusText);
        Assert.Equal(10, progress.TotalFiles);
        Assert.Equal(5, progress.ProcessedCount);
        Assert.Equal(4, progress.SuccessCount);
        Assert.Equal(1, progress.FailedCount);
        Assert.Equal(0, progress.SkippedCount);
        Assert.Equal("C:", progress.CurrentDrive);
        Assert.Equal("C:\\failed.iso", progress.FailedPathToAdd);
    }
}
