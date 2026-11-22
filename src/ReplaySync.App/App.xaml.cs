using System.Windows;

namespace DeepReplaySync;

public partial class App : Application
{
    Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two syncs would fight over Deepchart's Play and Pause
        _singleInstance = new Mutex(false, @"Local\DeepReplaySync.App", out bool first);
        if (!first)
        {
            MessageBox.Show("Deep Replay Sync is already running.", "Deep Replay Sync");
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        DispatcherUnhandledException += (_, args) =>
        {
            // The sync has its own thread: an error in the display must not end it and leave Deepchart unguarded
            args.Handled = true;
            window.ShowUiError(args.Exception.Message);
        };
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
