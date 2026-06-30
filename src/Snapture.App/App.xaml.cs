using System.Threading;
using System.Windows;

namespace Snapture.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance guard: a tray utility should only run once.
        _singleInstance = new Mutex(initiallyOwned: true, "Snapture.SingleInstance", out var isNew);
        if (!isNew)
        {
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
