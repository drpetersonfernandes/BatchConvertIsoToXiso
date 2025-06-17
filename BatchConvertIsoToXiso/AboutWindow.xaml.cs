using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace BatchConvertIsoToXiso;

public partial class AboutWindow
{
    // Bug Report API configuration (Duplicated from MainWindow/App - ideally shared)
    private const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string BugReportApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string ApplicationName = "BatchConvertIsoToXiso"; // <-- Application name changed

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
            // Notify developer using the BugReportService
            // Note: This creates a new instance. Ideally, this service would be injected or shared.
            var bugReportService = new BugReportService(
                BugReportApiUrl, // Use constants defined in this file
                BugReportApiKey,
                ApplicationName);

            _ = bugReportService.SendBugReportAsync($"Error opening URL: {e.Uri.AbsoluteUri}. Exception: {ex.Message}");

            // Dispose the service after sending the report (or let it be GC'd if fire-and-forget)
            // A better pattern would be to pass the service in or use a static instance.
            // For simplicity following the template's pattern:
            bugReportService.Dispose();


            // Notify user
            MessageBox.Show($"Unable to open link: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // Mark the event as handled
        e.Handled = true;
    }

    private string GetApplicationVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "Unknown";
    }
}
