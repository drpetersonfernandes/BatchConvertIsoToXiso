using System.IO;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private void DisplayInitialInstructions()
    {
        _logger.LogMessage("Welcome to the Batch Convert ISO to XISO.");
        _logger.LogMessage("");
        _logger.LogMessage("This application provides three main functions, available in the tabs above:");
        _logger.LogMessage("1. Convert: Converts standard Xbox ISO files to the optimized XISO format. It can also process ISOs found within .zip, .7z, and .rar archives. It can also process CUE/BIN files.");
        _logger.LogMessage("2. Test Integrity: Verifies the integrity of your .iso files.");
        _logger.LogMessage("3. Explorer: Explore the content of .iso files.");
        _logger.LogMessage("");
        _logger.LogMessage("IMPORTANT: This tool ONLY works with Xbox and Xbox 360 ISO files.");
        _logger.LogMessage("It cannot convert or test ISOs from PlayStation, PlayStation 2, or other consoles.");
        _logger.LogMessage("");

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;

        var bchunkPath = Path.Combine(appDirectory, "bchunk.exe");
        if (File.Exists(bchunkPath))
        {
            _logger.LogMessage("INFO: bchunk.exe found. CUE/BIN conversion is enabled.");
        }
        else
        {
            _logger.LogMessage("WARNING: bchunk.exe not found. CUE/BIN conversion will fail.");
        }

        var sevenZipLibraryX64 = Path.Combine(appDirectory, "7z_x64.dll");
        var sevenZipLibraryArm64 = Path.Combine(appDirectory, "7z_arm64.dll");
        if (File.Exists(sevenZipLibraryX64) || File.Exists(sevenZipLibraryArm64))
        {
            _logger.LogMessage("INFO: SevenZipExtractor library found. Archive extraction is enabled.");
        }
        else
        {
            _logger.LogMessage("WARNING: SevenZipExtractor library not found. Archive extraction will fail.");
        }

        _logger.LogMessage("--- Ready ---");
    }
}