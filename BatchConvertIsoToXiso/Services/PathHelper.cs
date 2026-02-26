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

    /// <summary>
    /// Determines if the given path is a UNC (Universal Naming Convention) network path.
    /// Examples: \\server\share, \\server\share\folder\file.txt
    /// </summary>
    public static bool IsUncPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        return path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines if the given path is a network path (either UNC or a mapped network drive).
    /// </summary>
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        // Check for UNC path first
        if (IsUncPath(path)) return true;

        // Check if it's a mapped network drive
        try
        {
            var driveLetter = GetDriveLetter(path);
            if (string.IsNullOrEmpty(driveLetter)) return false;

            var driveInfo = new DriveInfo(driveLetter);
            return driveInfo.DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts the server and share name from a UNC path.
    /// Returns null if the path is not a valid UNC path.
    /// Example: \\server\share\folder -> (server: "server", share: "share")
    /// </summary>
    public static (string Server, string Share)? TryGetUncShareInfo(string? path)
    {
        if (!IsUncPath(path) || string.IsNullOrEmpty(path))
            return null;

        try
        {
            // Remove the leading \\
            var trimmed = path.Substring(2);

            // Split by backslash
            var parts = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                return (parts[0], parts[1]);
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

    /// <summary>
    /// Common network-related error messages that indicate transient network failures.
    /// These errors may be resolved by retrying the operation.
    /// </summary>
    public static readonly string[] NetworkErrorPatterns =
    [
        "network path was not found",
        "network name is no longer available",
        "the specified network name is no longer available",
        "an unexpected network error occurred",
        "the network location cannot be reached",
        "a device attached to the system is not functioning",
        "the semaphore timeout period has expired",
        "the network path was not found",
        "the specified server cannot perform the requested operation",
        "the remote procedure call failed",
        "the remote procedure call was cancelled",
        "the network bios session limit was exceeded",
        "network access is denied",
        "the network connection was aborted",
        "the network connection was reset",
        "the network is not present or not started",
        "the account is not authorized to login from this station",
        "logon failure: unknown user name or bad password",
        "the session was cancelled"
    ];

    /// <summary>
    /// Checks if an exception message contains network-related error patterns
    /// that suggest a transient network failure.
    /// </summary>
    public static bool IsNetworkError(Exception? exception)
    {
        if (exception == null) return false;

        var message = exception.Message;
        return NetworkErrorPatterns.Any(pattern =>
            message.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}