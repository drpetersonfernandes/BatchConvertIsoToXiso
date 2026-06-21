namespace BatchConvertIsoToXiso.Interfaces;

public interface IExternalToolService
{
    Task<string?> ConvertCueBinToIsoAsync(string cuePath, string tempOutputDir, CancellationToken token);
}