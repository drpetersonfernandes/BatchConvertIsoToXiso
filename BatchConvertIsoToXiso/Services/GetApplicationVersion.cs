using System.Reflection;

namespace BatchConvertIsoToXiso.Services;

public static class GetApplicationVersion
{
    public static string GetProgramVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "Unknown";
    }
}