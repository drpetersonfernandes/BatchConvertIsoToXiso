namespace BatchConvertIsoToXiso.interfaces;

public interface IExtractXisoService
{
    /// <summary>
    /// Converts an ISO file to XISO format using extract-xiso.exe
    /// </summary>
    /// <param name="inputFile">Path to the input ISO file</param>
    /// <param name="outputFolder">Folder where the converted XISO will be saved</param>
    /// <param name="skipSystemUpdate">Whether to skip the $SystemUpdate folder</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>True if conversion was successful, false otherwise</returns>
    Task<bool> ConvertIsoToXisoAsync(string inputFile, string outputFolder, bool skipSystemUpdate, CancellationToken token);
}
