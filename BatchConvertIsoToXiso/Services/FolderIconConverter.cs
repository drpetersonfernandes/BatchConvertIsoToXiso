using System.Globalization;
using System.Windows.Data;

namespace BatchConvertIsoToXiso.Services;

/// <summary>
/// Converter to show folder or file icons in the XIso Explorer
/// </summary>
public class FolderIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value is bool isDir && isDir) ? "📁" : "📄";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}