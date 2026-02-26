using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BatchConvertIsoToXiso.interfaces;
using BatchConvertIsoToXiso.Services;
using Microsoft.Extensions.DependencyInjection;
using BatchConvertIsoToXiso.Services.XisoServices;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;

namespace BatchConvertIsoToXiso;

public partial class App
{
    private const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string BugReportApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    public const string ApplicationName = "BatchConvertIsoToXiso";

    private IBugReportService? _bugReportService;
    private static IServiceProvider? ServiceProvider { get; set; }
    private IMessageBoxService? _messageBoxService;
    private ILogger? _logger;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);

            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            ServiceProvider = serviceCollection.BuildServiceProvider();

            _bugReportService = ServiceProvider.GetRequiredService<IBugReportService>();
            _messageBoxService = ServiceProvider.GetRequiredService<IMessageBoxService>();
            _logger = ServiceProvider.GetRequiredService<ILogger>();

            // Startup cleanup
            if (_logger != null)
            {
                await TempFolderCleanupHelper.CleanupBatchConvertTempFoldersAsync(_logger);
            }

            // Create and show the main window
            using var scope = ServiceProvider.CreateScope();
            var mainWindow = scope.ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            _ = ReportException(ex, "Bug OnStartup");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ServiceProvider != null)
        {
            var disposableServices = ServiceProvider.GetServices<object>()
                .Where(static s => s is IDisposable)
                .Cast<IDisposable>();

            foreach (var service in disposableServices)
            {
                try
                {
                    service.Dispose();
                }
                catch
                {
                    /* Ignore disposal errors */
                }
            }
        }

        if (ServiceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IBugReportService>(static _ => new BugReportService(BugReportApiUrl, BugReportApiKey, ApplicationName));
        services.AddSingleton<IUpdateChecker, UpdateChecker>();
        services.AddSingleton<ILogger, LoggerService>();
        services.AddSingleton<IDiskMonitorService, DiskMonitorService>();
        services.AddSingleton<IMessageBoxService, MessageBoxService>();
        services.AddSingleton<IUrlOpener, UrlOpenerService>();
        services.AddTransient<IFileExtractor, FileExtractorService>(static provider => new FileExtractorService(provider.GetRequiredService<ILogger>(), provider.GetRequiredService<IBugReportService>()));
        services.AddTransient<IFileMover, FileMoverService>(static provider => new FileMoverService(provider.GetRequiredService<ILogger>(), provider.GetRequiredService<IBugReportService>(), provider.GetRequiredService<IDiskMonitorService>()));
        services.AddTransient<AboutWindow>();
        services.AddSingleton<IExternalToolService, ExternalToolService>();
        services.AddSingleton<IExtractXisoService, ExtractXisoService>();
        services.AddSingleton<IOrchestratorService, OrchestratorService>();
        services.AddSingleton<INativeIsoIntegrityService, NativeIsoIntegrityService>();
        services.AddSingleton<XisoWriter>();
        services.AddTransient<MainWindow>();
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _ = ReportException(exception, "AppDomain.UnhandledException");
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _ = ReportException(e.Exception, "Application.DispatcherUnhandledException");
        e.Handled = true;
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _ = ReportException(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    private async Task ReportException(Exception exception, string source)
    {
        try
        {
            var message = BuildExceptionReport(exception, source);

            if (_bugReportService != null) await _bugReportService.SendBugReportAsync(message);

            // Inform the user that a critical error occurred
            await Current.Dispatcher.InvokeAsync(() => _messageBoxService?.ShowError("A critical error occurred and has been reported. The application may need to close."));
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
        sb.AppendLine();

        // Add exception details
        sb.AppendLine("Exception Details:");
        ExceptionFormatter.AppendExceptionDetails(sb, exception);

        return sb.ToString();
    }
}