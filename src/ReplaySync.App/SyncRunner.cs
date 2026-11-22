using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeepReplaySync;

/// <summary>
/// Runs the <see cref="SyncController"/> on its own MTA thread, so a busy UI can not delay a pause and a slow
/// UI Automation call can not freeze the window. The UI posts commands and reads the latest <see cref="Status"/>.
/// </summary>
sealed class SyncRunner
{
    /// <summary>What the UI sees. Timestamps are Stopwatch timestamps.</summary>
    public sealed record Status(SyncSnapshot Sync, long PublishedAt, long ActivitySince);

    readonly SyncOptions _options;
    readonly ConcurrentQueue<Action<SyncController>> _commands = new();
    readonly ConcurrentQueue<string> _log = new();
    readonly AutoResetEvent _wake = new(false);
    readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Thread _thread;

    SyncController? _controller;
    CancellationTokenSource? _cleanStart;
    string? _lastActivity;
    long _activitySince;
    volatile Status? _latest;
    volatile string? _fatal;
    volatile bool _stopping;
    volatile bool _cleanStarting;

    public SyncRunner(SyncOptions options)
    {
        _options = options;
        _thread = new Thread(Run) { IsBackground = true, Name = "Replay sync" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public Status? Latest => _latest;
    public string? Fatal => _fatal;
    /// <summary>From the request until it ended, also while it waits for the sync thread.</summary>
    public bool CleanStarting => _cleanStarting;
    public bool TryTakeLog(out string line) => _log.TryDequeue(out line!);

    public void Play() => Post(c => c.Play());
    public void Pause() => Post(c => c.Pause());

    public void ApplyTuning(Settings s)
    {
        // Read now, on the UI thread
        double margin = s.Margin, hysteresis = s.Hysteresis, catchUp = s.CatchUp;
        Post(c => c.ApplyTuning(margin, hysteresis, catchUp));
    }

    public void CleanStart(string text)
    {
        var cancel = new CancellationTokenSource();
        _cleanStart = cancel;
        _cleanStarting = true;
        Post(c =>
        {
            // Off while it runs, back on only when both replays really started
            c.SetCorrection(false);
            try
            {
                if (c.CleanStart(text, cancel.Token)) c.EnableSync();
                else Log("Clean start did not finish: the sync is off");
            }
            finally
            {
                _cleanStarting = false;
            }
        });
    }

    public void CancelCleanStart() => _cleanStart?.Cancel();

    /// <summary>
    /// Ends the sync and leaves Deepchart paused if it was syncing. The result is false when that pause failed.
    /// </summary>
    public Task<bool> StopAsync()
    {
        CancelCleanStart();
        _stopping = true;
        _wake.Set();
        return _finished.Task;
    }

    void Post(Action<SyncController> command)
    {
        _commands.Enqueue(command);
        _wake.Set();
    }

    void Run()
    {
        bool leftSafe = false;
        try
        {
            using var controller = new SyncController(_options, Log);
            _controller = controller;
            while (!_stopping)
            {
                while (!_stopping && _commands.TryDequeue(out var command))
                {
                    try { command(controller); }
                    catch (Exception e) { Log($"Command failed: {e.Message}"); }
                    Publish();
                }
                if (_stopping) break;
                controller.Step();
                Publish();
                _wake.WaitOne(_options.PollMs);
            }
            leftSafe = controller.DisableSync();
        }
        catch (Exception e)
        {
            _fatal = e.Message;
            Log($"The sync stopped: {e}");
        }
        finally
        {
            _controller = null;
            _finished.TrySetResult(leftSafe);
        }
    }

    // The controller logs at the start of each long operation, so logging also publishes what it is doing
    void Log(string message)
    {
        _log.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        if (Thread.CurrentThread == _thread) Publish();
    }

    void Publish()
    {
        if (_controller is not { } controller) return;
        var snapshot = controller.Snapshot();
        long now = Stopwatch.GetTimestamp();
        if (snapshot.Activity is not null && _lastActivity is null) _activitySince = now;
        _lastActivity = snapshot.Activity;
        _latest = new Status(snapshot, now, _activitySince);
    }
}
