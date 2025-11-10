using System.IO;

namespace BatchConvertIsoToXiso.Services;

public class CheckForTempPath
{
    /// <summary>
    /// Checks if the selected path is the system's temporary directory or a subfolder within it.
    /// </summary>
    /// <param name="selectedPath">The path selected by the user.</param>
    /// <returns>True if the path is the system temp folder or a subfolder, false otherwise.</returns>
    public static bool IsSystemTempPath(string selectedPath)
    {
        var systemTempPath = Path.GetTempPath();

        // Normalize both paths to ensure consistent comparison (e.g., handle trailing slashes)
        var normalizedSystemTempPath = Path.GetFullPath(systemTempPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedSelectedPath = Path.GetFullPath(selectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Check if the selected path is exactly the system temp path or starts with it (indicating a subfolder)
        return normalizedSelectedPath.Equals(normalizedSystemTempPath, StringComparison.OrdinalIgnoreCase) ||
               normalizedSelectedPath.StartsWith(normalizedSystemTempPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}