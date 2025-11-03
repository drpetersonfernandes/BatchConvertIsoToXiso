using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace BatchConvertIsoToXiso;

public partial class AboutWindow
{
    public AboutWindow()
    {
        InitializeComponent();

        AppVersionTextBlock.Text = $"Version: {GetApplicationVersion()}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });

            process?.Dispose();
        }
        catch (Exception ex)
        {
            var bugReportService = new BugReportService(
                App.BugReportApiUrl,
                App.BugReportApiKey,
                App.ApplicationName);

            _ = bugReportService.SendBugReportAsync($"Error opening URL: {e.Uri.AbsoluteUri}. Exception: {ex.Message}");

            bugReportService.Dispose();


            MessageBox.Show($"Unable to open link: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // Mark the event as handled
        e.Handled = true;
    }

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "Unknown";
    }
}