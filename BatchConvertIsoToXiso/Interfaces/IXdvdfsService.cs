namespace BatchConvertIsoToXiso.interfaces;

public interface IXdvdfsService
{
    Task<bool> ConvertIsoToXisoAsync(string inputFile, string outputFolder, CancellationToken token);
}