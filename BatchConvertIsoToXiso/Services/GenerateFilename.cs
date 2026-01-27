namespace BatchConvertIsoToXiso.Services;

public static class GenerateFilename
{
    public static string GenerateSimpleFilename(int fileIndex)
    {
        return $"iso_{fileIndex:D6}.iso";
    }
}