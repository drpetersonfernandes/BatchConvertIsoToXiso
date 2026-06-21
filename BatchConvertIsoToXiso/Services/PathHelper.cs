using System.IO;
using BatchConvertIsoToXiso.Interfaces;

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
        if (string.IsNullOrEmpty(path) || !IsUncPath(path))
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
    /// that suggest a transient network failure. Supports messages in multiple
    /// languages (English, German, French, Spanish, Italian).
    /// </summary>
    public static bool IsNetworkError(Exception? exception)
    {
        if (exception == null) return false;

        if (MatchesNetworkPatterns(exception.Message))
            return true;

        if (exception.InnerException != null && MatchesNetworkPatterns(exception.InnerException.Message))
            return true;

        return false;
    }

    private static bool MatchesNetworkPatterns(string message)
    {
        // English patterns
        if (NetworkErrorPatterns.Any(pattern => message.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            return true;

        // "device" can appear in non-network errors like "The device is not ready" (ERROR_NOT_READY)
        if (message.Contains("device", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("device is not ready", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // German
        if (message.Contains("Netzwerk", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("nicht mehr verfügbar", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // French
        if (message.Contains("réseau", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("n'est plus disponible", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Spanish — use contextual phrases to avoid matching English words like "redirect"
        if (message.Contains("la red", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("de red", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Italian
        if (message.Contains("rete", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines if an exception is related to disk space issues.
    /// Checks HResult codes for ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL,
    /// as well as multilingual error messages.
    /// </summary>
    public static bool IsDiskSpaceError(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            var hResult = ioEx.HResult & 0xFFFF;
            if (hResult is 0x70 or 0x27) return true; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        }

        if (ex.InnerException is IOException innerIoEx)
        {
            var hResult = innerIoEx.HResult & 0xFFFF;
            if (hResult is 0x70 or 0x27) return true;
        }

        var message = ex.Message;
        if (ex.InnerException != null)
        {
            message += " " + ex.InnerException.Message;
        }

        return message.Contains("Not enough space", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not enough disk space", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("insufficient disk space", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Disk full", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Espace insuffisant", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("disque plein", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a temporary directory path with sufficient disk space.
    /// First checks the system temp drive, then falls back to other local drives.
    /// </summary>
    public static string ResolveTempDirectory(long requiredSize, string tempSubfolder, IDiskMonitorService diskMonitorService)
    {
        var defaultTempPath = Path.GetTempPath();
        var defaultTempDriveRoot = Path.GetPathRoot(defaultTempPath);
        var requiredWithBuffer = requiredSize + Math.Max(requiredSize / 10, 200L * 1024 * 1024);

        if (defaultTempDriveRoot != null)
        {
            try
            {
                var defaultDrive = new DriveInfo(defaultTempDriveRoot);
                if (defaultDrive.IsReady && defaultDrive.AvailableFreeSpace >= requiredWithBuffer)
                    return Path.Combine(defaultTempPath, tempSubfolder, Guid.NewGuid().ToString());
            }
            catch
            {
                // Ignore and fall through to alternative search
            }
        }

        var altDrive = diskMonitorService.FindDriveWithFreeSpace(requiredSize, defaultTempDriveRoot);
        if (altDrive != null)
        {
            return Path.Combine(altDrive, tempSubfolder, Guid.NewGuid().ToString());
        }

        var requiredFormatted = Formatter.FormatBytes(requiredWithBuffer);
        var defaultAvailable = Formatter.FormatBytes(diskMonitorService.GetAvailableFreeSpace(defaultTempPath));
        throw new IOException($"Not enough disk space to create temporary files. Required: {requiredFormatted}, Available: {defaultAvailable}. No other local drives have sufficient free space. Please free up disk space and try again.");
    }
}
