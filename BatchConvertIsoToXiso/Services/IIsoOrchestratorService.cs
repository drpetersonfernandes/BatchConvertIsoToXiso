using BatchConvertIsoToXiso.Models;

namespace BatchConvertIsoToXiso.Services;

public enum CloudRetryResult
{
    Retry,
    Skip,
    Cancel
}

public interface IIsoOrchestratorService
{
    Task ConvertAsync(
        string inputFolder,
        string outputFolder,
        bool deleteOriginals,
        bool skipSystemUpdate,
        bool checkIntegrity,
        bool searchSubfolders,
        IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired,
        CancellationToken token);

    Task TestAsync(
        string inputFolder,
        bool moveSuccessful,
        bool moveFailed,
        bool searchSubfolders,
        IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired,
        CancellationToken token);
}