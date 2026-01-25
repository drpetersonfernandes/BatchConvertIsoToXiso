using System.IO;

namespace BatchConvertIsoToXiso.Services;

public static class PathHelper
{
    /// <summary>
    /// Extracts the drive letter (e.g., "C:") from a given path.
    /// </summary>
    public static string? GetDriveLetter(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            // Handle UNC paths (network shares) which don't have traditional drive letters
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return null;

            var fullPath = Path.GetFullPath(path);
            var pathRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(pathRoot)) return null;

            var driveInfo = new DriveInfo(pathRoot);
            return driveInfo.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }
}