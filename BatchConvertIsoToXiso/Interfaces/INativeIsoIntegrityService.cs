using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;

namespace BatchConvertIsoToXiso.Interfaces;

public interface INativeIsoIntegrityService
{
    Task<bool> TestIsoIntegrityAsync(string isoPath, bool performDeepScan, IProgress<BatchOperationProgress> progress, CancellationToken token);
    List<FileEntry> GetDirectoryEntries(IsoSt isoSt, FileEntry dir);
}