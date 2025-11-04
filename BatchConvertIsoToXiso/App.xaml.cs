using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BatchConvertIsoToXiso.Services;
using Microsoft.Extensions.DependencyInjection;
using SevenZip;

namespace BatchConvertIsoToXiso;

public partial class App
{
    public const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    public const string BugReportApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    public const string ApplicationName = "BatchConvertIsoToXiso";

    private IBugReportService? _bugReportService;
    public static IServiceProvider? ServiceProvider { get; private set; }

    public App()
    {
        // Set up global exception handling
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        ServiceProvider = serviceCollection.BuildServiceProvider();

        _bugReportService = ServiceProvider.GetRequiredService<IBugReportService>();

        InitializeSevenZipSharp();

        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IBugReportService>(new BugReportService(BugReportApiUrl, BugReportApiKey, ApplicationName));
        services.AddSingleton<IUpdateChecker, UpdateChecker>();
        services.AddSingleton<ILogger, LoggerService>();
        services.AddSingleton<IMessageBoxService, MessageBoxService>();
        services.AddSingleton<IUrlOpener, UrlOpenerService>();
        services.AddTransient<IFileExtractor, FileExtractorService>();
        services.AddTransient<IFileMover, FileMoverService>();

        services.AddTransient<AboutWindow>();
        services.AddSingleton<MainWindow>();
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ReportException(exception, "AppDomain.UnhandledException");
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportException(e.Exception, "Application.DispatcherUnhandledException");
        e.Handled = true;
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportException(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    private void ReportException(Exception exception, string source)
    {
        try
        {
            var message = BuildExceptionReport(exception, source);

            if (_bugReportService != null)
            {
                _ = _bugReportService.SendBugReportAsync(message);
            }
        }
        catch
        {
            // Ignore
        }
    }

    private static string BuildExceptionReport(Exception exception, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Error Source: {source}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Date and Time: {DateTime.Now}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS Version: {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $".NET Version: {Environment.Version}");
        sb.AppendLine();

        // Add exception details
        sb.AppendLine("Exception Details:");
        AppendExceptionDetails(sb, exception);

        return sb.ToString();
    }

    private static void AppendExceptionDetails(StringBuilder sb, Exception exception, int level = 0)
    {
        while (true)
        {
            var indent = new string(' ', level * 2);

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Type: {exception.GetType().FullName}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Message: {exception.Message}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Source: {exception.Source}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}StackTrace:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{exception.StackTrace}");

            // If there's an inner exception, include it too
            if (exception.InnerException != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Inner Exception:");
                exception = exception.InnerException;
                level += 1;
                continue;
            }

            break;
        }
    }

    private void InitializeSevenZipSharp()
    {
        try
        {
            var dllName = Environment.Is64BitProcess ? "7z_x64.dll" : "7z_x86.dll";
            var dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dllName);

            if (File.Exists(dllPath))
            {
                SevenZipBase.SetLibraryPath(dllPath);
            }
            else
            {
                var errorMessage = $"Could not find the required 7-Zip library: {dllName} in {AppDomain.CurrentDomain.BaseDirectory}";

                if (_bugReportService != null)
                {
                    _ = _bugReportService.SendBugReportAsync(errorMessage);
                }
            }
        }
        catch (Exception ex)
        {
            if (_bugReportService != null)
            {
                _ = _bugReportService.SendBugReportAsync(ex.Message);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Clean up event handlers
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;

        if (ServiceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
    }
}
