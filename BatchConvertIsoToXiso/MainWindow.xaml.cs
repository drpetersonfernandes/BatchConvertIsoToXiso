using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using BatchConvertIsoToXiso.Interfaces;
using BatchConvertIsoToXiso.Services;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private readonly IOrchestratorService _orchestratorService;
    private readonly IDiskMonitorService _diskMonitorService;
    private readonly INativeIsoIntegrityService _nativeIsoTester;
    private CancellationTokenSource _cts = new();
    private TaskCompletionSource _operationCompletedTcs = new();
    private readonly IUpdateChecker _updateChecker;
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly IUrlOpener _urlOpener;

    // Summary Stats
    private readonly Stopwatch _operationStopwatch = new();
    private readonly DispatcherTimer _processingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _uiTotalFiles;
    private int _uiSuccessCount;
    private int _uiFailedCount;
    private int _uiSkippedCount;
    private bool _isOperationRunning;
    private bool _isForceClosing;

    private int _invalidIsoErrorCount;
    private int _totalProcessedFiles;
    private readonly HashSet<string> _failedFilePaths = new(StringComparer.OrdinalIgnoreCase);

    // XIso Explorer State
    private IsoSt? _explorerIsoSt;
    private readonly object _explorerIsoStLock = new();
    private readonly Stack<FileEntry> _parentDirectoryStack = new();
    private readonly Stack<string> _explorerPathNames = new();
    private FileEntry? _currentDirectoryEntry;

    public MainWindow(IUpdateChecker updateChecker, ILogger logger, IBugReportService bugReportService,
        IMessageBoxService messageBoxService, IUrlOpener urlOpener,
        IOrchestratorService orchestratorService, IDiskMonitorService diskMonitorService, INativeIsoIntegrityService nativeIsoTester)
    {
        InitializeComponent();

        _updateChecker = updateChecker;
        _logger = logger;
        _bugReportService = bugReportService;
        _messageBoxService = messageBoxService;
        _urlOpener = urlOpener;
        _orchestratorService = orchestratorService;
        _diskMonitorService = diskMonitorService;
        _nativeIsoTester = nativeIsoTester;
        _logger.Initialize(LogViewer);
        _processingTimer.Tick += ProcessingTimer_Tick;
        _memoryTimer.Tick += MemoryTimer_Tick;

        ResetSummaryStats();
        DisplayInstructions.Initialize(_logger);
        DisplayInstructions.DisplayInitialInstructions();
    }

    private async void Window_LoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            // Set initial navigation button style
            UpdateNavigationButtonStyles(BtnNavConvert);

            try
            {
                await CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                _ = _bugReportService.SendBugReportAsync("Error checking for updates", ex);
            }
        }
        catch (Exception ex)
        {
            _ = _bugReportService.SendBugReportAsync("Error setting initial navigation button style", ex);
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_isForceClosing) return;

        if (_isOperationRunning)
        {
            var result = _messageBoxService.Show("An operation is still running. Exit anyway?", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }

            // Cancel the operation and prevent immediate window close
            e.Cancel = true;

            // Wait for the operation to complete in the background, then close
            _ = WaitForOperationAndCloseAsync();
            return;
        }

        // No operation running, safe to close immediately
        CleanupResources();
    }

    private async Task WaitForOperationAndCloseAsync()
    {
        _logger.LogMessage("Waiting for current operation to cancel before exiting...");

        // Signal the operation to stop
        _cts.Cancel();

        // Wait up to 10 seconds for the operation to complete
        var completedTask = await Task.WhenAny(_operationCompletedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        var timedOut = completedTask != _operationCompletedTcs.Task;
        if (timedOut)
        {
            _logger.LogMessage("Warning: Operation did not complete within timeout. Closing anyway.");
            timedOut = true;
        }
        else
        {
            _logger.LogMessage("Operation completed. Closing application...");
        }

        // Now perform cleanup and close on the UI thread
        await Dispatcher.InvokeAsync(() =>
        {
            _isForceClosing = true;
            CleanupResources();
            Close();
        });

        // If the operation timed out, the dispatcher may still be blocked by a
        // queued message box or another modal dialog. Force a process-level exit
        // after a delay as a last resort — this skips normal cleanup but prevents
        // a permanently hung process.
        if (timedOut || !_isForceClosing)
        {
            ThreadPool.QueueUserWorkItem(static _ =>
            {
                Thread.Sleep(5000);
                Environment.Exit(0);
            });
        }
    }

    private void CleanupResources()
    {
        lock (_explorerIsoStLock)
        {
            _explorerIsoSt?.Dispose();
        }
        _processingTimer.Stop();
        _memoryTimer.Stop();
        StopPerformanceCounter();

        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch
        {
            // Ignore disposal errors during shutdown
        }
    }

    private void MemoryTimer_Tick(object? sender, EventArgs e)
    {
        var memoryMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
        MemoryTextBlock.Text = $"Memory: {memoryMb:F1} MB";
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}