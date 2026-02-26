using System.Windows;
using BatchConvertIsoToXiso.Services;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            // Log the current running version
            var currentVersion = GetApplicationVersion.GetProgramVersion();
            _logger.LogMessage($"Application started. Current version: {currentVersion}");
            _logger.LogMessage("Checking for updates...");

            var (isNewVersionAvailable, latestVersion, downloadUrl) = await _updateChecker.CheckForUpdateAsync();

            if (isNewVersionAvailable && !string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(latestVersion))
            {
                _logger.LogMessage($"Update available! Version {latestVersion} is available on the release page.");
                _logger.LogMessage($"Current version: {currentVersion} | Available version: {latestVersion}");

                var result = _messageBoxService.Show($"A new version ({latestVersion}) is available. Would you like to go to the download page?",
                    "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    _urlOpener.OpenUrl(downloadUrl);
                }
            }
            else
            {
                _logger.LogMessage($"You are using the most updated version ({currentVersion}).");
            }
        }
        catch (Exception ex)
        {
            // Log and report the error, but don't bother the user.
            _logger.LogMessage($"Error checking for updates: {ex.Message}");
            _ = ReportBugAsync("Error during update check", ex);
        }
    }
}