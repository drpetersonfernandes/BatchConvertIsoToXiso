using BatchConvertIsoToXiso.Services.Xiso;

namespace BatchConvertIsoToXiso.Models;

public class XisoExplorerItem
{
    public string Name { get; set; } = string.Empty;
    public string SizeFormatted { get; set; } = string.Empty;
    public string Type => IsDirectory ? "Folder" : "File";
    public bool IsDirectory { get; set; }
    public FileEntry Entry { get; set; } = null!;
}