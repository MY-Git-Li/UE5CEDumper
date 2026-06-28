using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UE5DumpUI.Core;
using UE5DumpUI.Services;
using UE5DumpUI.ViewModels;
using UE5DumpUI.Views;

namespace UE5DumpUI;

public class App : Application
{
    // Service instances (simple composition root — no DI container for AOT compatibility)
    private WindowsPlatformService? _platform;
    private LoggingService? _logging;
    private IPipeClient? _pipeClient;
    private DumpService? _dumpService;
    private AobUsageService? _aobUsage;
    private AobMakerBridgeService? _aobMakerBridge;
    private ProxyDeployService? _proxyDeploy;
    private ExperimentalGate? _experimentalGate;
    private SnapshotStore? _snapshotStore;
    private UiOptionsStore? _uiOptions;
    private BookmarkStore? _bookmarkStore;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Single instance check
            _platform = new WindowsPlatformService();
            if (!_platform.TryAcquireSingleInstance())
            {
                // Another instance is running
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                desktop.Shutdown(1);
                return;
            }

            // Opt into Windows "restartable apps": if we're open when the user
            // reboots / installs an update, Windows relaunches us on next
            // sign-in (and the window-state restore below puts us back in place).
            _platform.RegisterForRestart();

            // Initialize services
            var logDir = _platform.GetLogDirectoryPath();
            _logging = new LoggingService(logDir);
            // Two-connection lane router (interactive + bulk) — see
            // LaneRoutingPipeClient / docs/multipipe-eval.md §9.
            _pipeClient = new LaneRoutingPipeClient(_logging);
            _dumpService = new DumpService(_pipeClient, _logging);
            _aobUsage = new AobUsageService(_platform, _logging);
            _aobMakerBridge = new AobMakerBridgeService(_logging);
            _proxyDeploy = new ProxyDeployService(_logging);
            _experimentalGate = new ExperimentalGate(_platform, _logging);
            _snapshotStore = new SnapshotStore(_platform, _logging);
            _uiOptions = new UiOptionsStore(_platform, _logging);
            _bookmarkStore = new BookmarkStore(_platform, _logging);

            _logging.Info(Constants.LogCatInit, "UE5DumpUI starting...");
            _logging.Info(Constants.LogCatInit, $"Version:   {typeof(App).Assembly.GetName().Version}");
            _logging.Info(Constants.LogCatInit, $"OS:        {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            _logging.Info(Constants.LogCatInit, $"Runtime:   {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            _logging.Info(Constants.LogCatInit, $"Arch:      {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            _logging.Info(Constants.LogCatInit, $"Log dir:   {logDir}");

            // Create main window
            var globalHotkeys = new WindowsGlobalHotkeyService();
            var mainVm = new MainWindowViewModel(
                _pipeClient, _dumpService, _logging, _platform, _aobUsage, _aobMakerBridge,
                _proxyDeploy, _experimentalGate, _snapshotStore, globalHotkeys, _bookmarkStore);

            // Load + apply persisted panel options, then track changes for
            // debounced save-on-change. Done before the window is shown so the
            // restored values are in place for the first render.
            mainVm.InitializeOptionsPersistence(_uiOptions);

            // Restore last-session window placement (position / size / maximized,
            // validated against the monitors present this session). Attached
            // before the window is shown so there's no visible reposition.
            var windowStateStore = new WindowStateStore(_platform);
            var mainWindow = new MainWindow { DataContext = mainVm };
            mainWindow.AttachWindowState(windowStateStore);
            desktop.MainWindow = mainWindow;

            desktop.ShutdownRequested += (_, _) =>
            {
                _logging?.Info(Constants.LogCatInit, "UE5DumpUI shutting down...");
                // Flush any pending debounced option change before teardown.
                mainVm.FlushOptions();
                _pipeClient?.Dispose();
                _aobMakerBridge?.Dispose();
                _platform?.Dispose();
                (_logging as IDisposable)?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

}
