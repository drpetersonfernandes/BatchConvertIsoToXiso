using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
    private const string StatsApiUrl = "https://www.purelogiccode.com/ApplicationStats/stats";
    public const string ApplicationName = "BatchConvertIsoToXiso";

    private IBugReportService? _bugReportService;
    private IStatsService? _statsService;
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
            _statsService = ServiceProvider.GetRequiredService<IStatsService>();

            // Startup cleanup
            if (_logger != null)
            {
                await TempFolderCleanupHelper.CleanupBatchConvertTempFoldersAsync(_logger);
            }

            _ = _statsService?.SendStatsAsync();

            // Create and show the main window with enhanced error handling
            try
            {
                using var scope = ServiceProvider.CreateScope();
                var mainWindow = scope.ServiceProvider.GetRequiredService<MainWindow>();
                mainWindow.Show();
            }
            catch (SEHException sehEx)
            {
                // Handle font/WPF rendering issues gracefully
                await HandleFontRenderingError(sehEx);
            }
            catch (InvalidOperationException opEx) when (opEx.Message.Contains("font", StringComparison.OrdinalIgnoreCase) ||
                                                         opEx.Message.Contains("FontFamily", StringComparison.OrdinalIgnoreCase))
            {
                // Handle font-related InvalidOperationException
                await HandleFontRenderingError(opEx);
            }
        }
        catch (SEHException sehEx)
        {
            // Handle SEH exceptions during service setup
            await HandleFontRenderingError(sehEx);
        }
        catch (Exception ex)
        {
            _ = ReportException(ex, "Bug OnStartup");
        }
    }

    private async Task HandleFontRenderingError(Exception ex)
    {
        _logger?.LogMessage($"Font/Rendering error during startup: {ex.Message}");

        var errorMessage =
            "The application encountered a font or rendering error during startup.\n\n" +
            "This issue commonly occurs when:\n" +
            "- Running on Windows 7 or older systems\n" +
            "- Running through Wine/Proton compatibility layers (e.g., on Steam Deck/Linux)\n" +
            "- System fonts are missing or corrupted\n\n" +
            "Recommended solutions:\n" +
            "1. Ensure 'Segoe UI' and 'Arial' fonts are installed\n" +
            "2. On Linux/Steam Deck: Install corefonts package via winetricks\n" +
            "   (winetricks corefonts)\n" +
            "3. Update your Wine/Proton version\n" +
            "4. On Windows: Run 'sfc /scannow' to repair system files\n\n" +
            $"Technical details: {ex.GetType().Name}\n" +
            $"Error: {ex.Message}";

        _messageBoxService?.ShowError(errorMessage);

        // Report this critical error
        await ReportException(ex, "Bug OnStartup - FontRenderingError");
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ServiceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IBugReportService>(static _ => new BugReportService(BugReportApiUrl, BugReportApiKey, ApplicationName));
        services.AddSingleton<IStatsService>(static _ => new StatsService(StatsApiUrl, BugReportApiKey, ApplicationName));
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
        services.AddSingleton<IXdvdfsService, XdvdfsService>();
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

        sb.AppendLine("=== Environment Details ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Name: {ApplicationName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Version: {GetApplicationVersion.GetProgramVersion()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS Version: {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Bitness: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Windows Version: {GetWindowsVersion()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Processor Count: {Environment.ProcessorCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Base Directory: {AppContext.BaseDirectory}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Temp Path: {Path.GetTempPath()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"User: {Environment.UserName}");
        sb.AppendLine();

        sb.AppendLine("=== Error Details ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Error Source: {source}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Exception Type: {exception.GetType().FullName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Error Message: {exception.Message}");
        sb.AppendLine();

        // Add exception details
        sb.AppendLine("Exception Details:");
        ExceptionFormatter.AppendExceptionDetails(sb, exception);

        return sb.ToString();
    }

    private static string GetWindowsVersion()
    {
        try
        {
            // Try to get Windows version from registry or environment
            var osVersion = Environment.OSVersion;
            if (osVersion.Platform == PlatformID.Win32NT)
            {
                return $"{osVersion.Version.Major}.{osVersion.Version.Minor}.{osVersion.Version.Build}";
            }

            return "N/A (Non-Windows)";
        }
        catch
        {
            return "Unknown";
        }
    }
}
