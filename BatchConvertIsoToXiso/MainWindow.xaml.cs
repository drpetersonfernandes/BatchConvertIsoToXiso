using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using BatchConvertIsoToXiso.interfaces;
using BatchConvertIsoToXiso.Services;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    private readonly IOrchestratorService _orchestratorService;
    private readonly IDiskMonitorService _diskMonitorService;
    private readonly INativeIsoIntegrityService _nativeIsoTester;
    private CancellationTokenSource _cts = new();
    private readonly IUpdateChecker _updateChecker;
    private readonly ILogger _logger;
    private readonly IBugReportService _bugReportService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly IUrlOpener _urlOpener;

    // Summary Stats
    private DateTime _operationStartTime;
    private readonly DispatcherTimer _processingTimer;
    private int _uiTotalFiles;
    private int _uiSuccessCount;
    private int _uiFailedCount;
    private int _uiSkippedCount;
    private bool _isOperationRunning;

    private int _invalidIsoErrorCount;
    private int _totalProcessedFiles;
    private readonly List<string> _failedFilePaths = [];

    // XIso Explorer State
    private IsoSt? _explorerIsoSt;
    private readonly Stack<FileEntry> _explorerHistory = new();
    private readonly Stack<string> _explorerPathNames = new();

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
        _processingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _processingTimer.Tick += ProcessingTimer_Tick;

        ResetSummaryStats();
        DisplayInstructions.Initialize(_logger);
        DisplayInstructions.DisplayInitialInstructions();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            _ = _bugReportService.SendBugReportAsync($"Error checking for updates: {ex.Message}");
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_isOperationRunning)
        {
            var result = _messageBoxService.Show("An operation is still running. Exit anyway?", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }

            _cts.Cancel();
        }

        _explorerIsoSt?.Dispose();
        _processingTimer.Stop();
        StopPerformanceCounter();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}