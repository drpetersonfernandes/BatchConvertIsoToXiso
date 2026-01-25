namespace BatchConvertIsoToXiso;

public partial class MainWindow : IDisposable
{
    // Add this helper method to MainWindow.BatchConvert.cs

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}