namespace BatchConvertIsoToXiso.Services;

public static class Formatter
{
    public static string FormatBytes(long bytes)
    {
        const long kilobyte = 1024;
        const long megabyte = kilobyte * 1024;
        const long gigabyte = megabyte * 1024;
        const long terabyte = gigabyte * 1024;

        return bytes switch
        {
            < kilobyte => $"{bytes} B",
            < megabyte => $"{bytes / kilobyte:F1} KB",
            < gigabyte => $"{bytes / megabyte:F1} MB",
            < terabyte => $"{bytes / gigabyte:F1} GB",
            _ => $"{bytes / terabyte:F1} TB"
        };
    }

    public static string FormatBytesPerSecond(float bytesPerSecond)
    {
        const int kilobyte = 1024;
        const int megabyte = kilobyte * 1024;

        switch (bytesPerSecond)
        {
            case < kilobyte:
                return $"{bytesPerSecond:F1} B/s";
            case < megabyte:
                return $"{bytesPerSecond / kilobyte:F1} KB/s";
            default:
                return $"{bytesPerSecond / megabyte:F1} MB/s";
        }
    }
}