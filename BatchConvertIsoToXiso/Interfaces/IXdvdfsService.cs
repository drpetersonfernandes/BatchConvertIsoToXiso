namespace BatchConvertIsoToXiso.Interfaces;

public interface IXdvdfsService
{
    Task<bool> ConvertIsoToXisoAsync(string inputFile, string outputFolder, CancellationToken token);
}