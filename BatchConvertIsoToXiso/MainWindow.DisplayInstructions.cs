using System.IO;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private void DisplayInitialInstructions()
    {
        _logger.LogMessage("Welcome to the Batch Convert ISO to XISO & Test Tool.");
        _logger.LogMessage("");
        _logger.LogMessage("This application provides two main functions, available in the tabs above:");
        _logger.LogMessage("1. Convert to XISO: Converts standard Xbox ISO files to the optimized XISO format. It can also process ISOs found within .zip, .7z, and .rar archives.");
        _logger.LogMessage("2. Test ISO Integrity: Verifies the integrity of your .iso files by attempting a full extraction to a temporary location.");
        _logger.LogMessage("");
        _logger.LogMessage("IMPORTANT: This tool ONLY works with Xbox and Xbox 360 ISO files.");
        _logger.LogMessage("It cannot convert or test ISOs from PlayStation, PlayStation 2, or other consoles.");
        _logger.LogMessage("");
        _logger.LogMessage("General Steps:");
        _logger.LogMessage("- Select the appropriate tab for the operation you want to perform.");
        _logger.LogMessage("- Use the 'Browse' buttons to select your source and destination folders.");
        _logger.LogMessage("- Configure the options for your chosen operation.");
        _logger.LogMessage("- Click the 'Start' button to begin.");
        _logger.LogMessage("");

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");
        if (File.Exists(extractXisoPath))
        {
            _logger.LogMessage("INFO: extract-xiso.exe found in the application directory.");
        }
        else
        {
            _logger.LogMessage("WARNING: extract-xiso.exe not found. ISO conversion and testing will fail.");
            _ = ReportBugAsync("extract-xiso.exe not found.");
        }

        var bchunkPath = Path.Combine(appDirectory, "bchunk.exe");
        if (File.Exists(bchunkPath))
        {
            _logger.LogMessage("INFO: bchunk.exe found. CUE/BIN conversion is enabled.");
        }
        else
        {
            _logger.LogMessage("WARNING: bchunk.exe not found. CUE/BIN conversion will fail.");
            _ = ReportBugAsync("bchunk.exe not found.");
        }

        _logger.LogMessage("INFO: Archive extraction uses the SevenZipExtractor library.");
        _logger.LogMessage("--- Ready ---");
    }
}