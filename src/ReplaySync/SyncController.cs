using System.Diagnostics;
using FlaUI.UIA3;

/// <summary>
/// The sync itself: the correction rule and the clean start, shared by the console and the desktop app.
///
/// Deepdom is where the trades happen, so syncing never touches its replay: only Deepchart is paused and
/// resumed (Deepdom only by the master Play / Pause and a clean start). The rule is one-sided: Deepchart must
/// never show what Deepdom has not played yet. Deepchart's time is
/// taken as an upper bound and Deepdom's as a lower bound, so every uncertainty holds Deepchart back rather than
/// letting it run ahead.
///
/// Not thread safe: create and call it on one MTA thread. Only the snapshots may be handed to other threads.
/// </summary>
sealed class SyncController : IDisposable
{
    readonly SyncOptions _options;
    readonly Action<string> _log;
    readonly UIA3Automation _automation;
    readonly ScreenClocks _screen;
    readonly DeepdomScreenTime _domScreen = new();
    readonly DeepchartScreenTime _chartScreen = new();
    readonly Tracked _chart, _dom;
    // One time base with the screen images
    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    bool _chartHeld;          // Deepchart paused by this tool
    bool _chartMissedRead;    // Deepchart could not be read since the last good reading: it may have been playing
    bool _chartTimeDoubtful;  // a clean start failed: Deepchart's box may show typed text
    bool _domUnconfirmed;     // Deepdom's box shows a time which may be typed text rather than a replay position
    int _domNotLoaded;        // polls in a row in which Deepdom was neither playing nor able to play
    int _pauses;
    double _heldSeconds;
    double _worstAhead = double.NegativeInfinity;
    int _violations;
    double _previousStep = Now, _nextConnect;
    string? _activity, _lastFault;

    sealed class Tracked(ReplayApp app, ReplayClock clock)
    {
        public ReplayApp App { get; } = app;
        public ReplayClock Clock { get; } = clock;
        public string Label => App.Label;
        public ReplayReading? Last;
        public double? LastAttempt;
        public bool LastOk;
        public double? UnresponsiveSince;
        public double ScreenFrom;   // older images show the time before a jump
        public double? FrameHandled;
        public bool ToTheSecond;    // the time is being read from the screen
    }

    public SyncController(SyncOptions options, Action<string> log)
    {
        _options = options;
        _log = log;
        _automation = new UIA3Automation
        {
            // A frozen app must not block the loop: Deepchart keeps playing while we wait for Deepdom
            ConnectionTimeout = TimeSpan.FromMilliseconds(options.FreezeMs),
            TransactionTimeout = TimeSpan.FromMilliseconds(options.FreezeMs),
        };
        // UI Automation focuses the target of every press by default, which takes the focus from whatever the user
        // is typing in. Presses work without it.
        _automation.NativeAutomation2.AutoSetFocus = 0;
        _chart = new(new ReplayApp("Deepchart", ChartProcess, _automation),
            new ReplayClock(upperBound: true, initialRate: 1.0));
        _dom = new(new ReplayApp("Deepdom", DomProcess, _automation),
            new ReplayClock(upperBound: false, initialRate: AssumedDomRate));
        _screen = new ScreenClocks(ChartProcess, DomProcess, log);
    }

    const string ChartProcess = "Deepchart", DomProcess = "Deepdom";
    // Only for the best guess until it is measured, never for the bound
    const double AssumedDomRate = 0.6;

    public bool SyncOn { get; private set; }

    /// <summary>One poll: connect, read both clocks, correct. Never throws: a failure holds Deepchart.</summary>
    public void Step()
    {
        try
        {
            double now = Now;
            if (_chartHeld) _heldSeconds += now - _previousStep;
            _previousStep = now;

            Connect(now);
            // Deepdom first: it is the reference
            Poll(_dom, now);
            Poll(_chart, now);

            if (SyncOn) Correct();
            else if (_chart.Last is { Playing: true }) _chartHeld = false; // resumed by hand while the sync was off
            _lastFault = null;
        }
        catch (Exception e)
        {
            _activity = null;
            if (SyncOn && _chart.App.Pause()) _chartHeld = true;
            if (_lastFault != e.Message) _log($"Sync error, holding Deepchart: {e.Message}");
            _lastFault = e.Message;
        }
    }

    void Connect(double now)
    {
        if (now < _nextConnect || (_chart.App.IsConnected && _dom.App.IsConnected)) return;
        _nextConnect = now + 2;
        foreach (var t in new[] { _chart, _dom })
        {
            if (t.App.IsConnected || !t.App.TryConnect()) continue;
            _log($"{t.Label}: connected to the Replay Manage window");
            t.LastOk = false;
        }
    }

    void Poll(Tracked t, double now)
    {
        bool isChart = t == _chart;
        if (!t.App.IsConnected)
        {
            if (isChart && t.Last is not null) _chartMissedRead = true;
            return;
        }

        double dt = t.LastAttempt is { } previous ? now - previous : 0;
        // Deepdom is credited since the last attempt only. Deepchart since the last good reading: it goes on playing
        // while it does not answer.
        if (!isChart) t.LastAttempt = now;

        var status = t.App.Read(out var reading);
        if (status != ReadStatus.Ok)
        {
            if (status == ReadStatus.Gone)
            {
                _log($"{t.Label}: Replay Manage window gone{(t.App.IsAlive ? "" : " (process exited)")}");
            }
            else if (t.UnresponsiveSince is null)
            {
                t.UnresponsiveSince = now;
                _log($"{t.Label}: not responding");
            }
            t.LastOk = false;
            if (isChart) _chartMissedRead = true;
            return;
        }

        if (t.UnresponsiveSince is { } since)
        {
            _log($"{t.Label}: responding again after {now - since:0.0}s");
            t.UnresponsiveSince = null;
        }

        bool wasPlaying = t.Last is { Playing: true };
        // Time while Deepdom did not answer is not credited: it may have stalled. Deepchart is always credited, and
        // as playing: whether it was paused during the gap is not known.
        // Deepdom's time read from the screen needs no extrapolation, and an assumed rate could be too high
        bool credit = isChart || (t.LastOk && !t.ToTheSecond);
        var credited = isChart && _chartMissedRead ? reading with { Playing = true } : reading;
        t.LastOk = true;
        t.Last = reading;
        if (isChart)
        {
            t.LastAttempt = now;
            _chartMissedRead = false;
        }

        var before = t.Clock.DisplayedMinute;
        bool rollover = t.Clock.Update(credited, dt, credit);
        var after = t.Clock.DisplayedMinute;

        if (!isChart)
        {
            // Text typed into Deepdom's box looks like a new time, and following it would show the future. A changed
            // time counts only once a rollover is seen while playing. At first sight: not while reset or loading.
            if (before is null && after is not null) _domUnconfirmed = !reading.Playing && !t.App.CanPlay;
            else if (after != before) _domUnconfirmed = !(rollover && (reading.Playing || wasPlaying));
        }

        bool confirmedRollover = rollover && (isChart || !_domUnconfirmed);
        if (before is not null && after != before && !confirmedRollover)
        {
            // Set, Forward, reset or typed: the images may still show the time before it
            t.ScreenFrom = now + 1;
            if (isChart) _chartScreen.Reset();
            else _domScreen.Reset();
        }
        if (confirmedRollover && !isChart && (reading.Playing || wasPlaying)) _domScreen.Rollover(after!.Value, Now);

        if (rollover && !_domUnconfirmed)
        {
            LogRollover(t);
        }
        else if (before is not null && after != before)
        {
            string note = !isChart && _domUnconfirmed ? " (Deepchart waits for Deepdom's next rollover)" : "";
            _log($"{t.Label}: replay time changed {before:HH:mm} -> {after:HH:mm}{note}");
        }

        // After the minute clock, which says which minute the seconds belong to
        if (!isChart) WatchForReset(reading);
        if (!isChart && !reading.Playing) _domScreen.Stopped();
        if (isChart) ReadChartScreen(reading);
        else ReadDomScreen(reading);
    }

    // Reset or loading: where it will be is unknown, also when the box and the chart still show the old time.
    // Twice in a row, as Play and Pause are both disabled for a moment when pressed.
    void WatchForReset(ReplayReading reading)
    {
        _domNotLoaded = reading.Playing || _dom.App.CanPlay ? 0 : _domNotLoaded + 1;
        if (_domNotLoaded != 2) return;
        _dom.Clock.Restart();
        _domScreen.Reset();
        _domUnconfirmed = true;
        _log("Deepdom was reset (Deepchart waits for its next rollover)");
    }

    void ReadDomScreen(ReplayReading reading)
    {
        if (_screen.Dom is not { } seen || seen.FrameAt < _dom.ScreenFrom || _dom.Clock.DisplayedMinute is not { } minute)
        {
            _dom.ToTheSecond = false;
            return;
        }
        if (seen.FrameAt == _dom.FrameHandled) return;
        _dom.FrameHandled = seen.FrameAt;

        DateTime? known = _dom.Clock.Anchored && !_domUnconfirmed ? _dom.Clock.Bound : null;
        var reached = _domScreen.Update(seen.Value, seen.FrameAt, minute, reading.Speed, known);
        if (_domScreen.WentBack)
        {
            // A restart at an earlier time within the displayed minute changes nothing in the Replay Manage window
            _dom.Clock.Restart();
            _domUnconfirmed = true;
            _log("Deepdom's chart went back in time (Deepchart waits for Deepdom's next rollover)");
        }
        _dom.ToTheSecond = reached is not null;
        if (reached is { } time && !_domUnconfirmed) _dom.Clock.Reached(time);
    }

    void ReadChartScreen(ReplayReading reading)
    {
        if (_screen.Chart is not { } seen || seen.FrameAt < _chart.ScreenFrom || _chart.Clock.DisplayedMinute is not { } minute)
        {
            _chart.ToTheSecond = false;
            return;
        }
        _chart.ToTheSecond = true;
        // Also without a new image: a paused countdown is used once it has settled
        if (_chartScreen.Update(seen.Value, seen.FrameAt, Now, minute, reading.Speed, reading.Playing) is { } limit)
            _chart.Clock.NotPast(limit.Upper, limit.Ticking);
    }

    void LogRollover(Tracked t)
    {
        var other = t == _chart ? _dom : _chart;
        if (!other.Clock.Anchored) return;

        double ahead = (_chart.Clock.Estimate - _dom.Clock.Estimate).TotalSeconds;
        if (t == _dom)
        {
            // Deepdom is exactly at the minute: how far ahead is Deepchart really (best guess, not bound)?
            _worstAhead = Math.Max(_worstAhead, ahead);
            if (ahead > 0) _violations++;
        }
        _log($"{t.Label} at {t.Clock.Estimate:HH:mm:ss} (exact), Deepchart ~{DescribeGap(ahead)}  [{t.Label} rate {t.Clock.Rate:0.00}]");
    }

    /// <summary>Deepchart's upper bound against Deepdom's lower bound. Null: not known, so hold.</summary>
    double? AheadBound =>
        _domUnconfirmed || _chart.Clock.DisplayedMinute is null || _dom.Clock.DisplayedMinute is null
            ? null
            : (_chart.Clock.Bound - _dom.Clock.Bound).TotalSeconds;

    // Only Deepchart is ever paused or resumed
    void Correct()
    {
        if (!_chart.App.IsConnected || _chart.Last is not { } chart) return;

        double? bound = AheadBound;
        bool mustHold = bound is null || bound > -_options.Margin;
        bool mayResume = bound is { } b && b <= -(_options.Margin + _options.Hysteresis);

        // Far behind (restart, Deepdom ahead after a Set): jump Deepchart to the minute Deepdom shows. Safe: Deepdom
        // is at or past its displayed minute. Forward only goes ahead, so this never moves Deepchart back.
        if (bound is { } far && far < -_options.CatchUp && _chart.App.CanForward
            && _dom.Last is { } dom && dom.Minute == _dom.Clock.DisplayedMinute
            && _dom.Clock.DisplayedMinute > _chart.Clock.DisplayedMinute)
        {
            CatchUp(dom, -far);
            return;
        }

        if (chart.Playing && mustHold)
        {
            if (!_chart.App.Pause()) return;
            if (!_chartHeld) _pauses++;
            if (_chartHeld) _log("Deepchart was resumed by hand while ahead: paused again");
            if (_options.Verbose) _log($"pause Deepchart ({bound:0.0}s)");
            _chartHeld = true;
            // Seen as paused from now on, without waiting for the next read
            _chart.Last = chart with { Playing = false };
        }
        // While syncing Deepchart always follows: both run in sync or nothing does
        else if (!chart.Playing && mayResume)
        {
            if (!_chart.App.Play()) return;
            if (_options.Verbose) _log($"resume Deepchart ({bound:0.0}s)");
            _chartHeld = false;
            _chart.Last = chart with { Playing = true };
        }
    }

    void CatchUp(ReplayReading dom, double behind)
    {
        var target = dom.Minute!.Value;
        _activity = $"Forwarding Deepchart to {target:HH:mm}";
        _log($"Deepchart is {behind:0}s behind: Forward to {target:HH:mm}");
        if (_chart.App.EnsurePlaying(false, 10))
        {
            _chartHeld = true;
            if (_chart.App.ForwardTo(dom.Text) is { } error)
            {
                _log($"Forward failed: {error}");
            }
            else
            {
                Thread.Sleep(TimeSpan.FromSeconds(_options.ForwardWait));
                _chart.Clock.AnchorAt(target);
                _chartScreen.Reset();
                _chart.ScreenFrom = Now;
                _chart.Last = _chart.Last!.Value with { Playing = false, Minute = target };
                _chart.LastAttempt = Now;
            }
        }
        _activity = null;
    }

    public void EnableSync()
    {
        if (SyncOn) return;
        SyncOn = true;
        _log("Sync on");
    }

    /// <summary>
    /// Turns the sync off and leaves Deepchart paused, as nothing holds it back from then on. False when Deepchart
    /// could not be paused.
    /// </summary>
    public bool DisableSync()
    {
        if (!SyncOn) return true;
        SyncOn = false;
        if (!_chart.App.IsConnected)
        {
            _log("Sync off. Deepchart's replay window is not connected, so it could not be paused");
            return false;
        }

        bool wasPlaying = _chart.Last is { Playing: true };
        if (!_chart.App.EnsurePlaying(false, 3))
        {
            _log("Sync off, but Deepchart could NOT be paused: pause it by hand");
            return false;
        }
        if (wasPlaying) _chartHeld = true;
        if (_chart.Last is { } last) _chart.Last = last with { Playing = false };
        _log("Sync off. Deepchart is left paused");
        return true;
    }

    /// <summary>
    /// The master Play: turns the sync on and plays Deepdom. Deepchart is not pressed here: the rule starts it as
    /// soon as it is safely behind.
    /// </summary>
    public bool Play()
    {
        if (!_chart.App.IsConnected || !_dom.App.IsConnected)
        {
            _log("Play: both Replay Manage windows must be open");
            return false;
        }
        if (!SyncOn && !_chart.App.EnsurePlaying(false, 3))
        {
            _log("Play: Deepchart does not answer, so nothing was started");
            return false;
        }
        if (!SyncOn && _chart.Last is { } chart) _chart.Last = chart with { Playing = false };

        if (!_dom.App.EnsurePlaying(true, 5))
        {
            _log("Play: Deepdom did not start (still loading, or reset?)");
            return false;
        }
        // The time spent pressing is not credited: when exactly it started is not known
        _dom.LastAttempt = Now;
        if (_dom.Last is { } dom) _dom.Last = dom with { Playing = true };
        EnableSync();
        _log("Play");
        return true;
    }

    /// <summary>
    /// The master Pause: Deepchart first, then Deepdom, then the sync is off. It stays on when Deepchart did not
    /// pause, as something still has to hold it.
    /// </summary>
    public bool Pause()
    {
        bool chartPaused = _chart.App.EnsurePlaying(false, 3);
        bool domPaused = _dom.App.EnsurePlaying(false, 5);
        if (chartPaused)
        {
            SyncOn = false;
            _chartHeld = false;
            if (_chart.Last is { } chart) _chart.Last = chart with { Playing = false };
        }
        if (domPaused && _dom.Last is { } dom) _dom.Last = dom with { Playing = false };

        if (chartPaused && domPaused) _log("Pause");
        if (!chartPaused) _log("Pause: Deepchart did NOT pause: pause it by hand");
        if (!domPaused) _log("Pause: Deepdom did NOT pause: pause it by hand");
        return chartPaused && domPaused;
    }

    public void ApplyTuning(double margin, double hysteresis, double catchUp)
    {
        _options.Margin = margin;
        _options.Hysteresis = hysteresis;
        _options.CatchUp = catchUp;
        _log($"Settings: margin {margin}s, hysteresis {hysteresis}s, catch-up {catchUp}s");
    }

    public void SetCorrection(bool on) => SyncOn = on;

    /// <summary>The console's A (off), Q and Ctrl+C.</summary>
    public void ReleaseChart(string why)
    {
        if (_chartHeld && _chart.App.Play()) _log($"Deepchart resumed ({why})");
        _chartHeld = false;
    }

    /// <summary>
    /// Moves both replays to the same minute and presses Play in both. The only thing here which resets Deepdom.
    /// On failure or cancellation Deepchart is paused.
    /// </summary>
    public bool CleanStart(string text, CancellationToken cancel = default)
    {
        if (ReplayApp.ParseMinute(text) is not { } target)
        {
            _log($"Clean start: can not read the date \"{text}\"");
            return false;
        }

        _chartHeld = false;
        _dom.LastOk = false;
        bool done = false;
        try
        {
            done = RunCleanStart(text, target, cancel);
        }
        catch (OperationCanceledException)
        {
            _log("Clean start: cancelled");
        }
        catch (Exception e)
        {
            _log($"Clean start: {e.Message}");
        }

        if (!done)
        {
            // Either box may now show the typed date instead of a replay position
            _domUnconfirmed = _chartTimeDoubtful = true;
            if (!_chart.App.IsConnected || !_chart.App.EnsurePlaying(false, 5))
                _log("Clean start: Deepchart could NOT be paused: pause it by hand");
        }
        _activity = null;
        return done;
    }

    bool RunCleanStart(string text, DateTime target, CancellationToken cancel)
    {
        ReplayApp chart = _chart.App, dom = _dom.App;

        Report("Clean start: looking for both Replay Manage windows");
        var timer = Stopwatch.StartNew();
        while (!((chart.IsConnected || chart.TryConnect()) && (dom.IsConnected || dom.TryConnect())))
        {
            if (timer.Elapsed.TotalSeconds > 30)
            {
                _log("Clean start: both Replay Manage windows must be open");
                return false;
            }
            Wait(0.5, cancel);
        }

        cancel.ThrowIfCancellationRequested();
        Report("Clean start: pausing both");
        if (!chart.EnsurePlaying(false, 10))
        {
            _log("Clean start: Deepchart does not pause, so Deepdom was not touched");
            return false;
        }
        if (dom.TryRead(out var domNow) && domNow.Playing) dom.Pause();
        Wait(0.3, cancel);

        // Deepdom first: Reset + Set is the slow and fragile part
        Report($"Clean start: Deepdom Reset + Set to {text}");
        if (dom.ResetAndSet(text) is { } domError)
        {
            _log($"Clean start: Deepdom: {domError}");
            return false;
        }
        cancel.ThrowIfCancellationRequested();

        // Deepchart: Forward when the target is ahead (fast, keeps the loaded data), otherwise Reset + Set
        bool forwarded = false;
        if (!_chartTimeDoubtful && chart.CanForward
            && chart.TryRead(out var chartNow) && chartNow.Minute is { } chartMinute && target > chartMinute)
        {
            Report($"Clean start: Deepchart Forward from {chartMinute:HH:mm} to {target:HH:mm}");
            if (chart.ForwardTo(text) is { } forwardError)
            {
                _log($"Clean start: Deepchart: {forwardError}");
                return false;
            }
            forwarded = true;
        }
        else
        {
            Report($"Clean start: Deepchart Reset + Set to {text}");
            if (chart.ResetAndSet(text) is { } chartError)
            {
                _log($"Clean start: Deepchart: {chartError}");
                return false;
            }
        }

        // Wait until both are loaded (Play enabled). Deepdom sometimes crashes here: notice it instead of hanging.
        Report("Clean start: loading, this can take minutes");
        timer.Restart();
        while (!(dom.CanPlay && chart.CanPlay))
        {
            foreach (var app in new[] { chart, dom })
            {
                if (app.IsAlive) continue;
                _log($"Clean start: {app.Label} exited (crashed?) while loading. Restart it and run again.");
                return false;
            }
            if (timer.Elapsed.TotalSeconds > _options.LoadTimeout)
            {
                _log($"Clean start: timed out after {_options.LoadTimeout:0}s (Deepchart {Loading(chart)}, Deepdom {Loading(dom)})");
                return false;
            }
            Wait(0.2, cancel);
        }
        _log($"Clean start: both loaded after {timer.Elapsed.TotalSeconds:0.0}s");

        // Forward has no feedback: give it the configured time, counted from the Forward press
        if (forwarded && timer.Elapsed.TotalSeconds < _options.ForwardWait)
            Wait(_options.ForwardWait - timer.Elapsed.TotalSeconds, cancel);

        // Both at the same whole minute: Play in Deepdom first, so Deepchart can only start after it
        cancel.ThrowIfCancellationRequested();
        Report("Clean start: pressing Play");
        if (!dom.EnsurePlaying(true, 30))
        {
            _log("Clean start: Deepdom did not start playing");
            return false;
        }
        cancel.ThrowIfCancellationRequested();
        double chartStarted = Now;
        if (!chart.EnsurePlaying(true, 30))
        {
            _log("Clean start: Deepchart did not start playing");
            return false;
        }

        // Both clocks are exactly at the start minute: anchor them, no waiting for a first rollover. Deepchart is
        // credited from the moment before its Play. Deepdom too: it was seen playing before that.
        StartedAt(_chart, target, text, chartStarted);
        StartedAt(_dom, target, text, chartStarted);
        _domUnconfirmed = _chartTimeDoubtful = _chartMissedRead = false;
        _domScreen.Reset();
        _chartScreen.Reset();
        // Images from before the Play can still show the time before the Reset
        _chart.ScreenFrom = _dom.ScreenFrom = Now + 1;
        _chart.ToTheSecond = _dom.ToTheSecond = false;
        _log("Clean start: both playing");
        return true;
    }

    static void StartedAt(Tracked t, DateTime target, string text, double since)
    {
        int speed = t.App.TryRead(out var r) ? r.Speed : t.Last?.Speed ?? 1;
        t.Clock.AnchorAt(target);
        t.Last = new ReplayReading(target, text, Playing: true, speed);
        t.LastAttempt = since;
        t.LastOk = true;
        t.UnresponsiveSince = null;
    }

    static string Loading(ReplayApp app) => app.CanPlay ? "ready" : "loading";

    void Report(string activity)
    {
        _activity = activity;
        _log(activity);
    }

    static void Wait(double seconds, CancellationToken cancel)
    {
        cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
        cancel.ThrowIfCancellationRequested();
    }

    public SyncSnapshot Snapshot() => new(
        Describe(_chart),
        Describe(_dom),
        SyncOn,
        _chartHeld,
        _domUnconfirmed,
        _dom.ToTheSecond && !_domScreen.Calibrated,
        !_domUnconfirmed && _chart.Clock.Anchored && _dom.Clock.Anchored
            ? (_chart.Clock.Estimate - _dom.Clock.Estimate).TotalSeconds
            : null,
        AheadBound,
        _pauses,
        _heldSeconds,
        double.IsNegativeInfinity(_worstAhead) ? null : _worstAhead,
        _violations,
        _activity);

    static AppSnapshot Describe(Tracked t) => new(
        t.Label,
        t.App.IsConnected,
        t.UnresponsiveSince is null,
        t.Last,
        t.Clock.DisplayedMinute,
        t.Clock.Estimate,
        t.Clock.Anchored,
        t.Clock.Rate,
        t.Clock.RateMeasured,
        t.ToTheSecond);

    public static string DescribeGap(double seconds) => seconds switch
    {
        > 0 => $"{seconds:0.0}s AHEAD of Deepdom",
        < 0 => $"{-seconds:0.0}s behind Deepdom",
        _ => "level with Deepdom",
    };

    public void Dispose()
    {
        _screen.Dispose();
        _automation.Dispose();
    }
}
