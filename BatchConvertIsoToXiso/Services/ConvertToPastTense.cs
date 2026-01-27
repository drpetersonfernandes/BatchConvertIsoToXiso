namespace BatchConvertIsoToXiso.Services;

public static class ConvertToPastTense
{
    public static string GetPastTense(string verb)
    {
        return verb.ToLowerInvariant() switch
        {
            "conversion" => "converted",
            "test" => "tested",
            _ => verb.ToLowerInvariant() + "ed"
        };
    }
}