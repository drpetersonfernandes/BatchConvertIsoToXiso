using BatchConvertIsoToXiso.Models;

namespace BatchConvertIsoToXiso.interfaces;

public interface IOrchestratorService
{
    Task ConvertAsync(
        string inputFolder,
        string outputFolder,
        bool deleteOriginals,
        bool skipSystemUpdate,
        bool checkIntegrity,
        bool searchSubfolders,
        bool useExtractXiso,
        IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired,
        CancellationToken token);

    Task TestAsync(
        string inputFolder,
        bool moveSuccessful,
        bool moveFailed,
        bool searchSubfolders,
        bool performDeepScan,
        IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired,
        CancellationToken token);
}