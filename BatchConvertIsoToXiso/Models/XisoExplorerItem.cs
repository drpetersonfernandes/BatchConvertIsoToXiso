using BatchConvertIsoToXiso.Services.Xiso;

namespace BatchConvertIsoToXiso.Models;

public class XisoExplorerItem
{
    public string Name { get; init; } = string.Empty;
    public string SizeFormatted { get; set; } = string.Empty;
    public string Type => IsDirectory ? "Folder" : "File";
    public bool IsDirectory { get; init; }

    // ReSharper disable once NullableWarningSuppressionIsUsed
    public FileEntry Entry { get; init; } = null!;
}