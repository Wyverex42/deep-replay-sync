using System.Diagnostics;
using FlaUI.UIA3;

/// <summary>
/// The sync itself: the correction rule and the clean start, shared by the console and the desktop app.
///
/// Deepdom is where the trades happen, so syncing never touches its replay: only Deepchart is paused and
/// resumed (Deepdom only by the master Play / Pause, a clean start and closing the app). The rule is one-sided:
/// Deepchart must never show what Deepdom has not played yet. Deepchart's time is
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
    bool _chartTimeDoubtful;  // a clean start typed into Deepchart's box and it was not seen loaded there since
    bool _chartReset;         // Deepchart waits for Set: it can not play
    bool _domUnconfirmed;     // Deepdom's box shows a time which may be typed text rather than a replay position
    DateTime? _domStartsAt;   // Deepdom is loaded at exactly this minute and was not seen playing since
    int _domNotLoaded;        // polls in a row in which Deepdom was neither playing nor able to play
    int _pauses;
    double _heldSeconds;
    double _worstAhead = double.NegativeInfinity;
    int _violations;
    double _previousStep = Now, _nextConnect;
    string? _activity, _lastFault;
    AxisReading? _tracedAxis;
    SpeedChange? _speedChange;
    int? _axisSecond;             // Deepdom's right-most axis label at the last reading
    bool _axisReadable;           // the last Deepdom screen reading had a label
    int _rolloversSinceAxisMoved; // Deepdom minute changes while playing since that label last moved
    bool _domChartStuck;          // Deepdom's box plays on but its chart does not move: the box is not what it shows
    int? _tracedCountdown;
    bool _boosting;               // Deepchart should play at BoostSpeed to close a gap at x1
    bool _boostSpeedSet;          // Deepchart's speed may have been raised for that and was not seen back at x1
    double? _boostPressedAt;
    double _boostStepSince;
    bool _boostTold;              // the failure to go back to x1 was logged
    int _chartBoxSpeed;           // Deepchart's speed box at the last reading
    int _settleSpeed;             // the speed it showed before it went down, credited until _settleUntil
    double _settleUntil;

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
        public (DateTime Minute, double At)? MovedTo; // Set or Forward by a clean start, not seen loaded there yet
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
        _chart = new(new ReplayApp(ChartLabel, ChartProcess, _automation),
            new ReplayClock(upperBound: true, initialRate: 1.0));
        _dom = new(new ReplayApp(DomLabel, DomProcess, _automation),
            new ReplayClock(upperBound: false, initialRate: AssumedDomRate));
        _screen = new ScreenClocks(ChartProcess, DomProcess, log);
    }

    const string ChartProcess = "Deepchart", DomProcess = "Deepdom";
    const string ChartLabel = "Deepchart", DomLabel = "Deepdom";
    public static string[] AppLabels => [ChartLabel, DomLabel];
    // Only for the best guess until it is measured, never for the bound
    const double AssumedDomRate = 0.6;
    // Seconds a clean start step may take, not counting the time in which the app does not answer
    const double StepTimeout = 60;
    // Loading disables all buttons at once, so a Set which is still enabled after this was not taken
    const double SetAgainAfter = 3;
    const double CatchUpPressSeconds = 5;
    // Play and Pause are both disabled for a moment when pressed, which reads as paused
    const double PausedFor = 1;
    /// <summary>The fastest speed offered: x200 froze Deepdom for 90 s and x400 stopped its chart (findings).</summary>
    public const int MaxSpeed = 100;
    // A speed which is not shown in the box after this long was not taken
    const double SpeedPressTimeout = 10, SpeedPressAgain = 1.5;
    // Slowing to x1: how long Deepdom may take to pass Deepchart before both are slowed anyway (then Deepchart waits)
    const double OvertakeTimeout = 30;
    // Deepdom's box changed minute this often while its axis label stood still: its chart is not moving
    const int StuckAfterRollovers = 2;
    // Catching up at x1: when Deepdom plays as fast as Deepchart a held Deepchart never gets back by itself, so it
    // plays at this speed from BoostFrom to BoostTo replay seconds beyond the resume point (margin + hysteresis)
    const int BoostSpeed = 2;
    const double BoostFrom = 3, BoostTo = 1;
    // A speed change is not instant: the old, higher speed is credited to Deepchart's bound this long after its box
    // shows a lower one
    const double SpeedSettle = 5;

    /// <summary>A speed change in progress. Apps are pressed in Order; HoldChart keeps Deepchart paused meanwhile.</summary>
    sealed class SpeedChange(int target, Queue<Tracked> order, bool holdChart, bool waitForOvertake)
    {
        public int Target { get; } = target;
        public Queue<Tracked> Order { get; } = order;
        public bool HoldChart { get; } = holdChart;
        public bool WaitForOvertake = waitForOvertake;
        public double StepSince = Now;
        public double? PressedAt;
    }

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

            AdvanceSpeedChange();
            Boost();
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
            t.MovedTo = null;
            if (t == _dom) _domStartsAt = null;
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

        // Before the clocks see the new minute: where a clean start moved it needs no confirmation
        if (t.MovedTo is { } moved)
        {
            if (reading.Minute != moved.Minute || (isChart && reading.Playing))
            {
                t.MovedTo = null;
            }
            else if (reading.Playing || t.App.CanPlay)
            {
                LoadedAt(t, moved.Minute, moved.At);
                dt = 0;
            }
        }
        if (isChart) _chartReset = !reading.Playing && t.App.CanSet;

        bool wasPlaying = t.Last is { Playing: true };
        // Time while Deepdom did not answer is not credited: it may have stalled. Deepchart is always credited, and
        // as playing: whether it was paused during the gap is not known.
        // Deepdom's time read from the screen needs no extrapolation, and an assumed rate could be too high
        bool credit = isChart || (t.LastOk && !t.ToTheSecond);
        var credited = isChart && _chartMissedRead ? reading with { Playing = true } : reading;
        // The upper bound counts the fastest speed Deepchart may still be playing at
        if (isChart) credited = credited with { Speed = ChartSpeedForBound(reading.Speed) };
        if (_options.Trace && (t.Last is not { } prev || prev.Text != reading.Text || prev.Playing != reading.Playing || prev.Speed != reading.Speed))
            _log($"trace {t.Label} box '{reading.Text}' {(reading.Playing ? "play" : "paused")} x{reading.Speed} at {now:0.000}");
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
            else if (after != before)
                _domUnconfirmed = !(rollover && (reading.Playing || wasPlaying)) && !PlausibleSkip(before, after, reading, wasPlaying, dt);
        }

        bool confirmedRollover = rollover && (isChart || !_domUnconfirmed);
        bool skipped = !isChart && before is not null && after != before && !_domUnconfirmed && !rollover;
        if (before is not null && after != before && !confirmedRollover && !skipped)
        {
            // Set, Forward, reset or typed: the images may still show the time before it
            t.ScreenFrom = now + 1;
            if (isChart) _chartScreen.Reset();
            else _domScreen.Reset();
        }
        if (confirmedRollover && !isChart && (reading.Playing || wasPlaying)) _domScreen.Rollover(after!.Value, Now);
        if ((confirmedRollover || skipped) && !isChart) CountDomMinute();

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
        if (!isChart)
        {
            WatchForReset(reading);
            WatchForStart(reading);
        }
        if (!isChart && !reading.Playing) _domScreen.Stopped();
        if (isChart) ReadChartScreen(reading with { Speed = credited.Speed });
        else ReadDomScreen(reading);
    }

    /// <summary>
    /// The speed to credit Deepchart's upper bound with: the box, but also the boost speed from before it is pressed,
    /// and a higher speed for SpeedSettle seconds after the box went down from it.
    /// </summary>
    int ChartSpeedForBound(int box)
    {
        double now = Now;
        if (box < _chartBoxSpeed)
        {
            _settleSpeed = now < _settleUntil ? Math.Max(_settleSpeed, _chartBoxSpeed) : _chartBoxSpeed;
            _settleUntil = now + SpeedSettle;
        }
        _chartBoxSpeed = box;
        int speed = box;
        if (now < _settleUntil) speed = Math.Max(speed, _settleSpeed);
        if (_boostSpeedSet) speed = Math.Max(speed, BoostSpeed);
        return speed;
    }

    /// <summary>
    /// Catching up at x1. On a PC where Deepdom keeps up with its speed, every second Deepchart was held is lost for
    /// good: Deepchart can only be paused, so it plays at BoostSpeed until it is close again. Only Deepchart's speed is
    /// changed. The pause rule stays as it is: the bound is credited at the boost speed (ChartSpeedForBound).
    /// </summary>
    void Boost()
    {
        if (!_chart.App.IsConnected || _chart.Last is not { } chart) return;

        double resumeAt = _options.Margin + _options.Hysteresis;
        bool wanted = SyncOn && _speedChange is null && !FastForward && chart.Playing && !_chartReset
                      && _dom.Last is { Speed: 1, Playing: true }
                      && AheadBound is { } behind && behind < -(resumeAt + (_boosting ? BoostTo : BoostFrom));
        if (wanted && !_boosting && chart.Speed == 1)
        {
            _boosting = _boostSpeedSet = true;
            _boostStepSince = Now;
            _boostPressedAt = null;
            _log($"Deepchart is {-AheadBound!.Value:0.0}s behind: catching up at x{BoostSpeed}");
        }
        else if (!wanted && _boosting)
        {
            _boosting = false;
            _boostStepSince = Now;
            _boostPressedAt = null;
            if (_options.Verbose) _log("Deepchart back to x1");
        }

        // A speed change of the user sets Deepchart itself
        if (_speedChange is not null || !_boostSpeedSet) return;
        // Set by hand: not ours to undo
        if (chart.Speed is not (1 or BoostSpeed))
        {
            _boosting = _boostSpeedSet = false;
            return;
        }

        int target = _boosting ? BoostSpeed : 1;
        if (chart.Speed == target)
        {
            _boostPressedAt = null;
            if (target == 1)
            {
                _boostSpeedSet = false;
                if (_boostTold) _log("Deepchart is back at x1");
                _boostTold = false;
            }
            return;
        }
        if (_boostPressedAt is { } pressed && Now - pressed < SpeedPressAgain) return;
        if (Now - _boostStepSince > SpeedPressTimeout)
        {
            if (_boosting)
            {
                _log($"Deepchart did not take x{BoostSpeed}: not catching up");
                _boosting = false;
                _boostStepSince = Now;
            }
            else if (!_boostTold)
            {
                // Still credited at the boost speed, so it is held more often but never ahead
                _log("Deepchart does not go back to x1: trying on. Set it by hand if this stays");
                _boostTold = true;
            }
        }
        // A timed out press may still be carried out: the reading decides, not the result
        if (_boosting) _chart.App.TypeSpeed(BoostSpeed);
        else _chart.App.PressSpeedPreset(1);
        _boostPressedAt = Now;
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
        _domStartsAt = null;
        _log("Deepdom was reset (Deepchart waits for its next rollover)");
    }

    /// <summary>
    /// The app is loaded where a clean start moved it, so it is at exactly that minute. Unlike typed text this needs
    /// no rollover to confirm it: only a Set which was taken gets a reset replay loaded again.
    /// </summary>
    void LoadedAt(Tracked t, DateTime minute, double movedAt)
    {
        t.MovedTo = null;
        t.Clock.AnchorAt(minute);
        t.ToTheSecond = false;
        if (t == _chart)
        {
            _chartScreen.Reset();
            // A paused chart is not redrawn, so also the image from the move counts. It can only raise the bound.
            t.ScreenFrom = movedAt;
            _chartTimeDoubtful = _chartMissedRead = _chartReset = false;
        }
        else
        {
            _domScreen.Reset();
            t.ScreenFrom = Now + 1; // older images can show the time before the Reset
            _domUnconfirmed = false;
            _domNotLoaded = 0;
            _domStartsAt = minute;
        }
        _log($"{t.Label} is loaded at {minute:HH:mm:ss} (exact)");
    }

    // Played from exactly a minute: as good as a rollover to calibrate the axis, so there is no waiting for one
    void WatchForStart(ReplayReading reading)
    {
        if (_domStartsAt is not { } start) return;
        if (reading.Minute != start || _domUnconfirmed)
        {
            _domStartsAt = null;
        }
        else if (reading.Playing)
        {
            _domScreen.Rollover(start, Now);
            _domStartsAt = null;
        }
    }

    void ReadDomScreen(ReplayReading reading)
    {
        if (_screen.Dom is not { } seen || seen.FrameAt < _dom.ScreenFrom || _dom.Clock.DisplayedMinute is not { } minute)
        {
            _dom.ToTheSecond = false;
            AxisSeen(null);
            return;
        }
        if (seen.FrameAt == _dom.FrameHandled) return;
        _dom.FrameHandled = seen.FrameAt;
        AxisSeen(seen.Value.SecondOfDay);
        if (_options.Trace && seen.Value != _tracedAxis)
        {
            _tracedAxis = seen.Value;
            var a = seen.Value;
            _log($"trace Deepdom axis {TimeSpan.FromSeconds(a.SecondOfDay):hh\\:mm\\:ss} step {a.StepSeconds}s frame {seen.FrameAt:0.000}");
        }

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
        if (_options.Trace && seen.Value != _tracedCountdown)
        {
            _tracedCountdown = seen.Value;
            _log($"trace Deepchart countdown {seen.Value} frame {seen.FrameAt:0.000}");
        }
        // Also without a new image: a paused countdown is used once it has settled
        if (_chartScreen.Update(seen.Value, seen.FrameAt, Now, minute, reading.Speed, reading.Playing) is { } limit)
            _chart.Clock.NotPast(limit.Upper, limit.Ticking);
    }

    void LogRollover(Tracked t)
    {
        var other = t == _chart ? _dom : _chart;
        if (!other.Clock.Anchored) return;

        double ahead = (_chart.Clock.Estimate - _dom.Clock.Estimate).TotalSeconds;
        // In fast forward a little ahead is accepted: only x1 counts
        if (t == _dom && !FastForward)
        {
            // Deepdom is exactly at the minute: how far ahead is Deepchart really (best guess, not bound)?
            _worstAhead = Math.Max(_worstAhead, ahead);
            if (ahead > 0) _violations++;
        }
        _log($"{t.Label} at {t.Clock.Estimate:HH:mm:ss} (exact), Deepchart ~{DescribeGap(ahead)}  [{t.Label} rate {t.Clock.Rate:0.00}]");
    }

    /// <summary>
    /// At high speed the box can skip minutes between two polls. A forward change while playing fast, no further than
    /// the replay could have played, is progress, not a Set or typed text.
    /// </summary>
    static bool PlausibleSkip(DateTime? before, DateTime? after, ReplayReading reading, bool wasPlaying, double dt) =>
        before is { } b && after is { } a && a > b && reading.Playing && wasPlaying && reading.Speed > 1
        && (a - b).TotalSeconds <= dt * reading.Speed * 1.5 + 60;

    // Deepdom changed minute while playing: its axis label should have moved since the last one
    void CountDomMinute()
    {
        // Counted only while the axis is read: without it the box is all there is (as before)
        if (!_axisReadable) return;
        _rolloversSinceAxisMoved++;
        if (_rolloversSinceAxisMoved < StuckAfterRollovers || _domChartStuck) return;
        _domChartStuck = true;
        _log("Deepdom's chart is not moving while its replay plays on (frozen, or scrolled back): Deepchart waits");
    }

    // Each new axis reading: a label which moved clears the suspicion. Unreadable: nothing to compare.
    // Only a label which moved clears it: a frozen chart may also fail to read now and then
    void AxisSeen(int? secondOfDay)
    {
        _axisReadable = secondOfDay is not null;
        if (secondOfDay is null || secondOfDay == _axisSecond) return;
        _axisSecond = secondOfDay;
        _rolloversSinceAxisMoved = 0;
        if (!_domChartStuck) return;
        _domChartStuck = false;
        _log("Deepdom's chart moves again");
    }

    /// <summary>Both apps play faster than x1 and no slowdown is under way: Deepchart is kept close, not behind.</summary>
    bool FastForward => _speedChange is not { HoldChart: true }
                        && _dom.Last is { Speed: > 1 } && _chart.Last is { Speed: > 1 };

    /// <summary>Fast forward: Deepchart is kept within plus or minus half of this many replay seconds of Deepdom.</summary>
    double FastBand => Math.Max(10, 2 * _options.FastLeadSeconds * (_dom.Last?.Speed ?? 1));

    /// <summary>Best guesses, also unanchored ones (minutes skipped at speed). Null: not known, so hold.</summary>
    double? FastAhead =>
        _domUnconfirmed || _domChartStuck || _chart.Clock.DisplayedMinute is null || _dom.Clock.DisplayedMinute is null
            ? null
            : (_chart.Clock.Estimate - _dom.Clock.Estimate).TotalSeconds;

    /// <summary>Deepchart's upper bound against Deepdom's lower bound. Null: not known, so hold.</summary>
    double? AheadBound =>
        _domUnconfirmed || _domChartStuck || _chart.Clock.DisplayedMinute is null || _dom.Clock.DisplayedMinute is null
            ? null
            : (_chart.Clock.Bound - _dom.Clock.Bound).TotalSeconds;

    // Only Deepchart is ever paused or resumed
    void Correct()
    {
        if (!_chart.App.IsConnected || _chart.Last is not { } chart) return;
        // Reset or still loading: Play and Forward are disabled, and a typed date would look like its time
        if (!chart.Playing && (_chartReset || _chart.MovedTo is not null)) return;

        double? bound = AheadBound;
        bool mustHold = bound is null || bound > -_options.Margin;
        bool mayResume = bound is { } b && b <= -(_options.Margin + _options.Hysteresis);
        bool fast = FastForward;

        if (_speedChange is { HoldChart: true })
        {
            (mustHold, mayResume) = (true, false);
        }
        else if (fast)
        {
            // Not trading at these speeds: the best guesses are good enough, a little ahead is acceptable
            double? ahead = FastAhead;
            mustHold = ahead is null || ahead > FastBand / 2;
            mayResume = ahead is { } a && a < -FastBand / 2;
            bound = ahead;
        }

        // Far behind (restart, Deepdom ahead after a Set): jump Deepchart to the minute Deepdom shows. Safe: Deepdom
        // is at or past its displayed minute. Forward only goes ahead, so this never moves Deepchart back.
        if (!fast && bound is { } far && far < -_options.CatchUp && _chart.App.CanForward
            && _dom.Last is { } dom && dom.Minute == _dom.Clock.DisplayedMinute
            && _dom.Clock.DisplayedMinute > _chart.Clock.DisplayedMinute
            && (chart.Playing || _chart.App.CanPlay))
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
            // The date is typed first, so giving up after one timed out press would leave a time it is not at
            double pressing = Now;
            string? error;
            while ((error = _chart.App.ForwardTo(dom.Text)) is not null && Now - pressing < CatchUpPressSeconds)
                Thread.Sleep(300);
            if (error is not null)
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
    /// Closing the app: pauses both and turns the sync off. Waits for an app which does not answer, while it keeps
    /// the other one paused. Returns the apps which may still be playing.
    /// </summary>
    public string[] PauseForExit(double seconds)
    {
        SyncOn = false;
        Report("Closing: pausing both");
        Tracked[] both = [_chart, _dom];
        var pausedSince = new double?[both.Length];
        double start = Now;
        bool toldWaiting = false;

        while (true)
        {
            for (int i = 0; i < both.Length; i++)
            {
                if (PauseIfPlaying(both[i].App)) pausedSince[i] ??= Now;
                else pausedSince[i] = null;
            }
            if (pausedSince.All(since => Now - since >= PausedFor))
            {
                _log("Closing: both are paused");
                return [];
            }

            var pending = both.Where((_, i) => pausedSince[i] is null).Select(t => t.Label).ToArray();
            if (pending.Length > 0)
            {
                string names = string.Join(" and ", pending);
                if (Now - start > seconds)
                {
                    _log($"Closing: could NOT pause {names}");
                    return pending;
                }
                if (!toldWaiting && Now - start > 2)
                {
                    toldWaiting = true;
                    Report($"Closing: waiting for {names} to pause");
                }
            }
            Thread.Sleep(250);
        }
    }

    /// <summary>True when it is seen paused or there is no replay. Presses Pause otherwise.</summary>
    static bool PauseIfPlaying(ReplayApp app)
    {
        if (!app.IsConnected && !app.TryConnect()) return !app.WindowSeen || !app.IsRunning;
        return app.TryRead(out var reading) && Paused(app, reading);
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

    /// <summary>
    /// Sets both replays to this speed: a click of the user, like the master Play. Deepchart is never the faster one
    /// while it happens: going up Deepdom is pressed first, going down Deepchart. Slowing to x1 while syncing holds
    /// Deepchart and lets Deepdom pass it at the old speed before both are slowed, so trading starts with Deepdom
    /// certainly ahead.
    /// </summary>
    public bool RequestSpeed(int speed)
    {
        if (speed is < 1 or > MaxSpeed)
        {
            _log($"Speed: x{speed} is not offered (x1 to x{MaxSpeed})");
            return false;
        }
        if (_chart.Last is not { } chart || _dom.Last is not { } dom)
        {
            _log("Speed: both Replay Manage windows must be open");
            return false;
        }

        bool slowToTrade = speed == 1 && SyncOn && (dom.Speed > 1 || chart.Speed > 1);
        bool overtake = slowToTrade && dom.Playing && dom.Speed > 1;
        var order = new Queue<Tracked>(speed < dom.Speed ? [_chart, _dom] : [_dom, _chart]);
        _speedChange = new SpeedChange(speed, order, holdChart: slowToTrade, waitForOvertake: overtake);
        _log(overtake ? $"Speed x1: Deepchart waits until Deepdom has passed it at x{dom.Speed}"
            : $"Speed x{speed}: {order.Peek().Label} first");
        return true;
    }

    void AdvanceSpeedChange()
    {
        if (_speedChange is not { } change) return;

        if (change.WaitForOvertake)
        {
            bool domPlaying = _dom.Last is { Playing: true };
            bool passed = _chart.Last is { Playing: false } && AheadBound is { } b && b <= -(_options.Margin + _options.Hysteresis);
            bool late = Now - change.StepSince > OvertakeTimeout;
            if (domPlaying && !passed && !late) return;
            _log(passed ? "Deepdom has passed Deepchart: slowing both to x1"
                : late ? $"Deepdom did not pass Deepchart within {OvertakeTimeout:0} s: slowing anyway, Deepchart waits"
                : "Deepdom is paused: slowing both to x1");
            change.WaitForOvertake = false;
            change.StepSince = Now;
        }

        while (change.Order.TryPeek(out var t))
        {
            if (t.Last?.Speed == change.Target)
            {
                change.Order.Dequeue();
                change.PressedAt = null;
                change.StepSince = Now;
                continue;
            }
            if (Now - change.StepSince > SpeedPressTimeout)
            {
                _log($"Speed: {t.Label} did not take x{change.Target}. Set it by hand");
                _speedChange = null;
                return;
            }
            if (change.PressedAt is not { } pressed || Now - pressed > SpeedPressAgain)
            {
                // A timed out press may still be carried out: the reading decides, not the result
                if (Array.IndexOf(ReplayApp.Presets, change.Target) >= 0) t.App.PressSpeedPreset(change.Target);
                else t.App.TypeSpeed(change.Target);
                change.PressedAt = Now;
            }
            return;
        }
        _log($"Both at x{change.Target}");
        _speedChange = null;
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
    /// Moves both replays to the same minute and plays Deepdom. The only thing here which resets Deepdom.
    /// Deepchart is left to the rule, which starts it once Deepdom is seen past the minute: playChart is only for
    /// running without the sync. On failure or cancellation Deepchart is paused.
    /// </summary>
    public bool CleanStart(string text, CancellationToken cancel = default, bool playChart = false)
    {
        if (ReplayApp.ParseMinute(text) is not { } target)
        {
            _log($"Clean start: can not read the date \"{text}\"");
            return false;
        }

        _chartHeld = false;
        _dom.LastOk = false;
        _dom.MovedTo = _chart.MovedTo = null;
        _domStartsAt = null;
        bool done = false;
        try
        {
            done = RunCleanStart(text, target, playChart, cancel);
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
            if (!_chart.App.IsConnected || !_chart.App.EnsurePlaying(false, 5))
                _log("Clean start: Deepchart could NOT be paused: pause it by hand");
        }
        _activity = null;
        return done;
    }

    bool RunCleanStart(string text, DateTime target, bool playChart, CancellationToken cancel)
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

        // The apps freeze for seconds at any of these steps, so each one waits and looks before it tries again
        if (!Until(chart, "Clean start: pausing Deepchart", StepTimeout, cancel, r => Paused(chart, r))) return false;
        if (!Until(dom, "Clean start: pausing Deepdom", StepTimeout, cancel, r => Paused(dom, r))) return false;

        // Deepdom first: Reset + Set is the slow and fragile part
        _domUnconfirmed = true; // from here on its box can show typed text, until it is loaded
        if (!ResetAndSet(_dom, text, target, cancel)) return false;

        // Deepchart: Forward when the target is ahead (fast, keeps the loaded data), otherwise Reset + Set. A reset
        // or loading Deepchart has no Forward.
        DateTime? chartMinute = null;
        bool chartLoaded = false;
        if (!Until(chart, "Clean start: reading Deepchart's time", _options.LoadTimeout, cancel, r =>
            {
                chartMinute = r.Minute;
                chartLoaded = chart.CanPlay;
                return chartLoaded || chart.CanSet;
            }))
        {
            return false;
        }

        double? forwardedAt = null;
        if (chartLoaded && !_chartTimeDoubtful && chart.CanForward && target > chartMinute)
        {
            _chartTimeDoubtful = true;
            if (!TypeDate(chart, text, target, cancel)) return false;
            double pressedFrom = Now;
            // Pressing it again is harmless: Forward only ever goes ahead
            if (!Until(chart, $"Clean start: Deepchart Forward to {text}", StepTimeout, cancel, _ => chart.Forward()))
                return false;
            _chart.MovedTo = (target, pressedFrom);
            forwardedAt = Now;
        }
        else
        {
            _chartTimeDoubtful = true;
            if (!ResetAndSet(_chart, text, target, cancel)) return false;
        }

        // Wait until both are loaded (Play enabled). Deepdom sometimes crashes here: notice it instead of hanging.
        double loadingSince = Now;
        foreach (var t in new[] { _dom, _chart })
        {
            if (!WaitLoaded(t, target, cancel)) return false;
        }
        _log($"Clean start: both loaded after {Now - loadingSince:0.0}s");

        // Forward has no feedback: give it the configured time, counted from the Forward press
        if (forwardedAt + _options.ForwardWait - Now is { } left and > 0) Wait(left, cancel);

        // Both are at exactly the minute, whatever fails from here on
        foreach (var t in new[] { _dom, _chart })
        {
            LoadedAt(t, target, t.MovedTo?.At ?? Now);
            t.Last = new ReplayReading(target, text, Playing: false, t.Last?.Speed ?? 1);
            t.LastAttempt = Now;
            t.LastOk = true;
            t.UnresponsiveSince = null;
        }

        if (!Until(dom, "Clean start: starting Deepdom", StepTimeout, cancel, r => Started(dom, r))) return false;
        // The time spent pressing is not credited to Deepdom, but it is to Deepchart
        double started = Now;
        _dom.LastAttempt = started;
        WatchForStart(new ReplayReading(target, text, Playing: true, _dom.Last?.Speed ?? 1));

        if (playChart)
        {
            if (!Until(chart, "Clean start: starting Deepchart", StepTimeout, cancel, r => Started(chart, r))) return false;
            _chart.LastAttempt = started;
            _log("Clean start: both playing");
        }
        else
        {
            // Pressed right away it was ahead whenever Deepdom froze at its start
            _chartHeld = true;
            _log("Clean start: Deepdom plays, Deepchart follows");
        }
        return true;
    }

    static bool Paused(ReplayApp app, ReplayReading reading)
    {
        if (!reading.Playing) return true;
        app.Pause();
        return false;
    }

    static bool Started(ReplayApp app, ReplayReading reading)
    {
        if (reading.Playing) return true;
        app.Play();
        return false;
    }

    bool ResetAndSet(Tracked t, string text, DateTime target, CancellationToken cancel)
    {
        var app = t.App;
        bool resetPressed = false;
        // Not a quick step: Deepchart's Reset took more than a minute
        if (!Until(app, $"Clean start: {app.Label} Reset", _options.LoadTimeout, cancel, _ =>
            {
                if (app.CanSet) return true;
                if (!resetPressed) resetPressed = app.Reset();
                return false;
            }))
        {
            return false;
        }
        if (!TypeDate(app, text, target, cancel)) return false;

        // Already now: the press can be carried out after a timeout or a cancel. It only counts once seen loaded,
        // which a reset replay can not be without a Set.
        t.MovedTo = (target, Now);
        double? pressedAt = null;
        bool setTried = false;
        // The press returns before the click is handled, so only the disabled button shows that it was taken
        return Until(app, $"Clean start: {app.Label} Set to {text}", _options.LoadTimeout, cancel, _ =>
        {
            if (setTried && app.SetIsDisabled) return true;
            if (pressedAt is { } at && Now - at < SetAgainAfter) return false;
            setTried = true;
            if (app.Set()) pressedAt = Now;
            return false;
        });
    }

    bool WaitLoaded(Tracked t, DateTime target, CancellationToken cancel)
    {
        var app = t.App;
        double pressedAt = Now;
        return Until(app, $"Clean start: waiting for {app.Label} to load, this can take minutes",
            _options.LoadTimeout, cancel, reading =>
            {
                if (app.CanPlay) return true;
                // Back in the reset state: the load did not work
                if (reading.Minute == target && Now - pressedAt >= SetAgainAfter && app.CanSet)
                {
                    _log($"Clean start: {app.Label} is not loading, pressing Set again");
                    app.Set();
                    pressedAt = Now;
                }
                return false;
            });
    }

    bool TypeDate(ReplayApp app, string text, DateTime target, CancellationToken cancel)
    {
        bool typed = false;
        return Until(app, $"Clean start: typing the date into {app.Label}", StepTimeout, cancel, reading =>
        {
            if (typed && reading.Minute == target) return true;
            typed = true;
            app.TypeDate(text);
            return false;
        });
    }

    /// <summary>
    /// Repeats the attempt until it returns true. It is only made when the app answers, and time in which the app
    /// does not answer is not counted against the timeout: only cancelling or the end of a process ends that wait.
    /// </summary>
    bool Until(ReplayApp app, string step, double timeout, CancellationToken cancel, Func<ReplayReading, bool> attempt)
    {
        Report(step);
        var tracked = app == _chart.App ? _chart : _dom;
        bool frozen = false;
        double counted = 0, previous = Now;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var either in new[] { _chart.App, _dom.App })
            {
                if (either.IsAlive) continue;
                _log($"Clean start: {either.Label} exited (crashed?). Restart it and run again.");
                return false;
            }

            var status = app.Read(out var reading);
            // Not polled meanwhile, and after a cancel it may not answer. Not the minute: that can be typed text.
            if (status == ReadStatus.Ok && tracked.Last is { } last) tracked.Last = last with { Playing = reading.Playing };
            if (status == ReadStatus.Ok && attempt(reading)) return true;
            if (status == ReadStatus.Gone) app.TryConnect();

            if (frozen != (status == ReadStatus.Unresponsive))
            {
                frozen = !frozen;
                Report(frozen ? $"{step} ({app.Label} is not responding, waiting)" : step);
            }
            if (!frozen) counted += Now - previous;
            previous = Now;
            if (counted > timeout)
            {
                _log($"{step}: gave up after {timeout:0}s");
                return false;
            }
            Wait(0.3, cancel);
        }
    }

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
        _chartReset,
        _dom.ToTheSecond && !_domScreen.Calibrated,
        !_domUnconfirmed && _chart.Clock.Anchored && _dom.Clock.Anchored
            ? (_chart.Clock.Estimate - _dom.Clock.Estimate).TotalSeconds
            : null,
        AheadBound,
        _pauses,
        _heldSeconds,
        double.IsNegativeInfinity(_worstAhead) ? null : _worstAhead,
        _violations,
        _activity,
        _speedChange?.Target,
        _speedChange is { WaitForOvertake: true },
        FastForward,
        FastForward ? FastAhead : null,
        FastBand,
        _domChartStuck,
        _boostSpeedSet);

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
