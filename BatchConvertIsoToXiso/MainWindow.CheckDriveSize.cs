using System.IO;
using BatchConvertIsoToXiso.Services;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private bool CheckDriveSpace(string path, long requiredSpace, string operationDescription)
    {
        var driveLetter = GetDriveLetter(path);
        if (string.IsNullOrEmpty(driveLetter))
        {
            _messageBoxService.ShowError($"Could not determine drive for {operationDescription} path '{path}'. Cannot proceed.");
            _ = ReportBugAsync($"Could not determine drive for {operationDescription} path '{path}'.");
            return false;
        }

        DriveInfo driveInfo;
        try
        {
            driveInfo = new DriveInfo(driveLetter);
        }
        catch (ArgumentException ex)
        {
            _messageBoxService.ShowError($"Invalid drive specified for {operationDescription} path '{path}': {ex.Message}. Cannot proceed.");
            _ = ReportBugAsync($"Invalid drive for {operationDescription} path '{path}'. Exception: {ex.Message}", ex);
            return false;
        }

        if (!driveInfo.IsReady)
        {
            _messageBoxService.ShowError($"{operationDescription} drive '{driveLetter}' is not ready. Cannot proceed.");
            _ = ReportBugAsync($"{operationDescription} drive '{driveLetter}' not ready.");
            return false;
        }

        if (driveInfo.AvailableFreeSpace < requiredSpace)
        {
            _messageBoxService.ShowError($"Insufficient free space on {operationDescription} drive ({driveLetter}). Required (estimated): {Formatter.FormatBytes(requiredSpace)}, Available: {Formatter.FormatBytes(driveInfo.AvailableFreeSpace)}. Please free up space.");
            _ = ReportBugAsync($"Insufficient space on {operationDescription} drive {driveLetter}. Available: {driveInfo.AvailableFreeSpace}, Required: {requiredSpace}");
            return false;
        }

        _logger.LogMessage($"INFO: {operationDescription} drive '{driveLetter}' has {Formatter.FormatBytes(driveInfo.AvailableFreeSpace)} free space (estimated required: {Formatter.FormatBytes(requiredSpace)}).");
        return true;
    }
}