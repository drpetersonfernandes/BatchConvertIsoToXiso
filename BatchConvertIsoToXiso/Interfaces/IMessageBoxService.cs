using System.Windows;

namespace BatchConvertIsoToXiso.interfaces;

public interface IMessageBoxService
{
    MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon);
    void ShowError(string message);
    void ShowWarning(string message, string title);
}