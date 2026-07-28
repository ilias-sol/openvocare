using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OpenVocare.Models;
using OpenVocare.Services;

namespace OpenVocare;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF owns the lifecycle.")]
public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\OpenVocare.SingleInstance.v5";
    private const string ShowSettingsEventName = "Local\\OpenVocare.ShowSettings.v5";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showSettingsEvent;
    private CancellationTokenSource? _showSettingsCancellation;
    private Task? _showSettingsListener;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths paths = new();
        AppLog.Initialize(paths.LogsDirectory);
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        AppLog.Write($"Single-instance check completed. CreatedNew={createdNew}.");
        if (!createdNew)
        {
            bool signaled = false;
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowSettingsEventName, out EventWaitHandle? showSettings))
                {
                    using (showSettings) signaled = showSettings.Set();
                }
            }
            catch (WaitHandleCannotBeOpenedException) { }
            bool shownDirectly = ShowExistingSettingsWindow();
            AppLog.Write($"Existing-instance activation requested. Signaled={signaled}, shownDirectly={shownDirectly}.");
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Environment.Exit(0);
            return;
        }

        try
        {
            StartSecondInstanceListener();
            SettingsStore store = new(paths);
            AppSettings settings = store.LoadAsync().GetAwaiter().GetResult();
            AppLog.Write("Creating the settings window.");
            MainWindow window = new(settings, store, new TranscriptHistoryStore(paths));
            MainWindow = window;
            AppLog.Write("Showing the settings window.");
            window.Show();
            AppLog.Write($"Settings window show returned. Visible={window.IsVisible}, loaded={window.IsLoaded}.");
        }
        catch (Exception exception)
        {
            AppLog.Write("Application startup failed.", exception);
            System.Windows.MessageBox.Show(
                $"OpenVocare could not start.\n\n{exception.Message}",
                "OpenVocare startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSettingsCancellation?.Cancel();
        _showSettingsListener?.Wait(TimeSpan.FromSeconds(1));
        _showSettingsCancellation?.Dispose();
        _showSettingsEvent?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void StartSecondInstanceListener()
    {
        _showSettingsEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ShowSettingsEventName);
        _showSettingsCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _showSettingsCancellation.Token;
        _showSettingsListener = Task.Run(() =>
        {
            WaitHandle[] handles = [_showSettingsEvent, cancellationToken.WaitHandle];
            while (WaitHandle.WaitAny(handles) == 0)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is MainWindow window) window.ShowFromSecondInstance();
                });
            }
        });
    }

    private static bool ShowExistingSettingsWindow()
    {
        IntPtr window = Native.FindWindow(null, "OpenVocare");
        if (window == IntPtr.Zero)
        {
            return false;
        }

        bool shown = Native.ShowWindow(window, Native.SwRestore);
        _ = Native.SetForegroundWindow(window);
        return shown;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("Unhandled user-interface exception.", e.Exception);
        e.Handled = true;
        System.Windows.MessageBox.Show(
            "OpenVocare encountered an unexpected error and needs to close. A local diagnostic log was saved.",
            "OpenVocare error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        AppLog.Write("Unhandled application exception.", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Write("Unobserved background task exception.", e.Exception);
        e.SetObserved();
    }

    private static class Native
    {
        public const int SwRestore = 9;

        [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string? className, string windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr window);
    }
}
