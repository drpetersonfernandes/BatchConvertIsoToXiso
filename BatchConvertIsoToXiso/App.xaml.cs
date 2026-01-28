using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BatchConvertIsoToXiso.interfaces;
using BatchConvertIsoToXiso.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;
using SevenZip;
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

            await CleanupTemporaryFolders();
            await InitializeSevenZipSharp();

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
        services.AddTransient<IFileMover, FileMoverService>(static provider => new FileMoverService(provider.GetRequiredService<ILogger>(), provider.GetRequiredService<IBugReportService>()));
        services.AddTransient<AboutWindow>();
        services.AddSingleton<IExternalToolService, ExternalToolService>();
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS Version: {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $".NET Version: {Environment.Version}");
        sb.AppendLine();

        // Add exception details
        sb.AppendLine("Exception Details:");
        ExceptionFormatter.AppendExceptionDetails(sb, exception);

        return sb.ToString();
    }

    private async Task InitializeSevenZipSharp()
    {
        try
        {
            var architecture = RuntimeInformation.ProcessArchitecture;

            // Check for 32-bit (x86) compatibility
            if (architecture == Architecture.X86)
            {
                const string errorMessage = "This application is not compatible with 32-bit (x86) Windows. Please use the 64-bit version of the application.";
                _logger?.LogMessage($"[ERROR] {errorMessage}");
                _messageBoxService?.ShowError(errorMessage);
                return;
            }

            var isArm64 = architecture == Architecture.Arm64;
            var dllName = isArm64 ? "7z_arm64.dll" : "7z_x64.dll";

            var dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dllName);

            if (File.Exists(dllPath))
            {
                SevenZipBase.SetLibraryPath(dllPath);
            }
            else
            {
                // Inform the user about the issue.
                var userErrorMessage = $"Could not find the required 7-Zip library: {dllName}. " +
                                       $"Please ensure '{dllName}' is in the same folder as the application. " +
                                       "Archive extraction features (.zip, .7z, .rar) will not work.";
                _messageBoxService?.ShowError(userErrorMessage);
            }
        }
        catch (Exception ex)
        {
            if (_bugReportService != null)
            {
                await _bugReportService.SendBugReportAsync(ex.Message);
            }

            _messageBoxService?.ShowError($"An error occurred while initializing the archive extraction library: {ex.Message}");
        }
    }

    private async Task CleanupTemporaryFolders()
    {
        var tempPath = Path.GetTempPath();
        const string tempFolderPrefix = "BatchConvertIsoToXiso_";

        try
        {
            var directories = Directory.EnumerateDirectories(tempPath, tempFolderPrefix + "*", SearchOption.TopDirectoryOnly);
            foreach (var dir in directories)
            {
                try
                {
                    // Attempt to delete the directory and its contents recursively
                    Directory.Delete(dir, true);
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore
                }
                catch (IOException)
                {
                    // Ignore
                }
                catch (Exception ex)
                {
                    if (_bugReportService != null) await _bugReportService.SendBugReportAsync($"Error cleaning orphaned temp folder {dir}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            if (_bugReportService != null) await _bugReportService.SendBugReportAsync($"Error enumerating temp folders for cleanup: {ex.Message}");
        }
    }
}