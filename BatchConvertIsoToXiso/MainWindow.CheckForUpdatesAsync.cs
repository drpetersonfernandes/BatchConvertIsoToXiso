using System.Windows;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var (isNewVersionAvailable, latestVersion, downloadUrl) = await _updateChecker.CheckForUpdateAsync();

            if (isNewVersionAvailable && !string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(latestVersion))
            {
                var result = MessageBox.Show(this, $"A new version ({latestVersion}) is available. Would you like to go to the download page?",
                    "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    _urlOpener.OpenUrl(downloadUrl);
                }
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