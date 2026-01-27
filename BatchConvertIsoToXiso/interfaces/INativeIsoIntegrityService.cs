using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services.XisoServices;

namespace BatchConvertIsoToXiso.interfaces;

public interface INativeIsoIntegrityService
{
    Task<bool> TestIsoIntegrityAsync(string isoPath, IProgress<BatchOperationProgress> progress, CancellationToken token);
    List<FileEntry> GetDirectoryEntries(IsoSt isoSt, FileEntry dir);
}