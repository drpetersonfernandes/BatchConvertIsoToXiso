using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using BatchConvertIsoToXiso.Services;

namespace BatchConvertIsoToXiso;

public partial class AboutWindow
{
    private readonly IUrlOpener _urlOpener;

    public AboutWindow(IUrlOpener urlOpener)
    {
        _urlOpener = urlOpener;
        InitializeComponent();

        AppVersionTextBlock.Text = $"Version: {GetApplicationVersion()}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        _urlOpener.OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "Unknown";
    }
}
