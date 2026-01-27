namespace BatchConvertIsoToXiso.interfaces;

public interface IFileMover
{
    Task MoveTestedFileAsync(string sourceFile, string destinationFolder, string moveReason, CancellationToken token);
    Task RobustMoveFileAsync(string source, string dest, CancellationToken token);
}