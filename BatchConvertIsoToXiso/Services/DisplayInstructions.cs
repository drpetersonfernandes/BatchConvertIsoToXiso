using System.IO;
using BatchConvertIsoToXiso.interfaces;
using System.Runtime.InteropServices;

namespace BatchConvertIsoToXiso.Services;

public static class DisplayInstructions
{
    private static ILogger? _logger;

    public static void Initialize(ILogger logger)
    {
        _logger = logger;
    }

    public static void DisplayInitialInstructions()
    {
        if (_logger != null)
        {
            _logger.LogMessage("Welcome to 'Batch Convert ISO to XISO'.");
            _logger.LogMessage("");
            _logger.LogMessage("This application provides three main functions, available in the tabs above:");
            _logger.LogMessage("1. Convert: Converts standard Xbox ISO files to the optimized XISO format. Supports archives (.zip, .7z, .rar) and CUE/BIN files.");
            _logger.LogMessage("2. Test Integrity: Verifies the XDVDFS file system structure and sector readability.");
            _logger.LogMessage("   NOTE: This test checks if the ISO is structurally valid and readable. It does NOT perform data checksum (MD5/SHA) verification.");
            _logger.LogMessage("3. Explorer: Explore the content of .iso files.");
            _logger.LogMessage("");
            _logger.LogMessage("IMPORTANT: This tool ONLY works with Xbox and Xbox 360 ISO files.");
            _logger.LogMessage("It cannot convert or test ISOs from PlayStation, PlayStation 2, or other consoles.");
            _logger.LogMessage("");

            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;

            var isArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

            if (isArm64)
            {
                _logger.LogMessage("WARNING: Running on ARM64. CUE/BIN conversion is disabled because the required tool (bchunk.exe) is not compatible with ARM64.");
            }
            else
            {
                var bchunkPath = Path.Combine(appDirectory, "bchunk.exe");
                if (File.Exists(bchunkPath))
                {
                    _logger.LogMessage("INFO: bchunk.exe found. CUE/BIN conversion is enabled.");
                }
                else
                {
                    _logger.LogMessage("WARNING: bchunk.exe not found. CUE/BIN conversion will fail.");
                }
            }

            // Check for extract-xiso.exe
            var extractXisoPath = Path.Combine(appDirectory, "extract-xiso.exe");
            if (File.Exists(extractXisoPath))
            {
                _logger.LogMessage("INFO: extract-xiso.exe found. XISO conversion is enabled.");
            }
            else
            {
                _logger.LogMessage("WARNING: extract-xiso.exe not found. XISO conversion will fail.");
            }

            _logger.LogMessage("INFO: Archive extraction is enabled (SharpCompress library loaded).");

            _logger.LogMessage("--- Ready ---");
        }
    }
}