using System.Runtime.CompilerServices;
using System.Text;

namespace BatchConvertIsoToXiso.Tests;

internal static class ModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
