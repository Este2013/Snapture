using System.Diagnostics;
using System.Threading;
using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Snapture.App;

public partial class App : Application
{
    private const string InstanceMutexName = "Snapture.SingleInstance";
    private const string ShowSettingsEventName = "Snapture.ShowSettings";

    private Mutex? _singleInstance;
    private AppController? _controller;
    private EventWaitHandle? _showSettingsSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance guard: a tray utility should only run once.
        _singleInstance = new Mutex(initiallyOwned: true, InstanceMutexName, out var isNew);
        if (!isNew)
        {
            // Already running: poke the live instance to show its settings, then exit.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowSettingsEventName, out var signal))
                {
                    signal.Set();
                    signal.Dispose();
                }
            }
            catch { /* best effort */ }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Snapture hit an unexpected error:\n\n" + args.Exception.Message,
                "Snapture", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true; // keep the tray app alive
        };

        _controller = new AppController();
        _controller.Startup();

        // Clicking a "saved" toast opens the captured file.
        ToastNotificationManagerCompat.OnActivated += toastArgs =>
        {
            var args = ToastArguments.Parse(toastArgs.Argument);
            if (args.Contains("action") && args["action"] == "update")
                Dispatcher.BeginInvoke(() => _controller?.ShowUpdateDialogFromToast());
            else if (args.TryGetValue("open", out var file))
                Dispatcher.BeginInvoke(() =>
                {
                    try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { }
                });
        };

        // Listen for later launches asking us to surface the settings window.
        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        var listener = new Thread(() =>
        {
            while (true)
            {
                _showSettingsSignal.WaitOne();
                Dispatcher.BeginInvoke(() => _controller?.ShowSettingsWindow());
            }
        })
        {
            IsBackground = true,
            Name = "Snapture.ShowSettingsSignal",
        };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _showSettingsSignal?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
