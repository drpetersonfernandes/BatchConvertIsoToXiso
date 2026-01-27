using System.Windows;
using System.Windows.Navigation;
using BatchConvertIsoToXiso.interfaces;
using BatchConvertIsoToXiso.Services;

namespace BatchConvertIsoToXiso;

public partial class AboutWindow
{
    private readonly IUrlOpener _urlOpener;
    private readonly IMessageBoxService _messageBoxService;

    public AboutWindow(IUrlOpener urlOpener, IMessageBoxService messageBoxService)
    {
        _urlOpener = urlOpener;
        _messageBoxService = messageBoxService;
        InitializeComponent();

        AppVersionTextBlock.Text = $"Version: {GetApplicationVersion.GetProgramVersion()}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            _urlOpener.OpenUrl(e.Uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            _messageBoxService.ShowError($"Unable to open link: {ex.Message}");
        }

        e.Handled = true;
    }
}