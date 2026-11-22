// The rules for when a time read from pixels may be trusted. No pixels and no Windows in here.

/// <summary>
/// Deepdom's lower bound from the right-most axis label.
///
/// The label sits at a fixed position a little left of "now" and advances in seconds, so it is the replay time
/// minus a constant which depends on the zoom (3.8 s at the zoom it was measured at), rounded down.
///
/// - The label itself is a lower bound as long as it is under played data.
/// - At a minute change the replay is exactly at hh:mm:00, which gives the constant: offset = minute - label. The
///   label was rounded down by up to one step, so label + offset - step is never ahead of the replay.
/// - After a zoom, a resize or a move the offset restarts from the bound known until then, which is safe in the same way.
/// - A label which jumps faster than the replay can play (chart dragged) is ignored until the next minute change.
/// - A label which moves back while playing is a chart scrolled into the past: ignored until it is back. Once
///   playback has stopped it is a restart at an earlier time.
/// </summary>
sealed class DeepdomScreenTime
{
    // Calibrate with an image from just after the minute change: a later label gives a smaller, safer offset
    const double CalibrateFrom = 0.4, CalibrateUntil = 3;
    const double MaxOffset = 180, JumpAllowance = 1.5;
    const int ViewTolerance = 3, SpacingTolerance = 6; // the spacing was seen from 65 to 69 px at one zoom
    const double ExpectedStepSlack = 0.2;
    const double Jitter = 1.5; // a restart moves further back than a redraw wobbles

    readonly Queue<double> _steps = new();
    AxisReading? _view;
    double? _offset;
    DateTime? _label, _rolloverMinute;
    double _labelAt, _rolloverAt;
    bool _untrusted;
    bool _stopped; // seen not playing since the last label

    public bool Calibrated => _offset is not null;
    /// <summary>The last update saw the time move back: a restart at an earlier time, or a scrolled chart.</summary>
    public bool WentBack { get; private set; }

    /// <summary>The time jumped (Set, reset, typed): start over.</summary>
    public void Reset()
    {
        _view = null;
        _label = _rolloverMinute = null;
        _offset = null;
        _untrusted = _stopped = false;
        _steps.Clear();
    }

    /// <summary>The minute clock changed while playing: the replay was exactly at this minute at that time.</summary>
    public void Rollover(DateTime minute, double at)
    {
        _rolloverMinute = minute;
        _rolloverAt = at;
        if (!_untrusted) return;
        _untrusted = false;
        _label = null;
    }

    /// <summary>Playback was seen stopped: only then can the time really go back within a minute.</summary>
    public void Stopped() => _stopped = true;

    /// <summary>A replay time Deepdom has certainly reached, or null.</summary>
    public DateTime? Update(AxisReading axis, double frameAt, DateTime minute, int speed, DateTime? knownReached = null)
    {
        var label = Nearest(minute, axis.SecondOfDay);
        WentBack = false;

        // Without the label left of it the zoom can not be checked: only the label itself is usable
        bool canCheckView = axis.StepSeconds > 0;
        if (canCheckView && _view is null)
        {
            _view = axis;
        }
        // Compared with the view at calibration, so that many small zooms can not add up unnoticed
        else if (canCheckView && ViewChanged(_view!.Value, axis))
        {
            _view = axis;
            _label = null;
            _rolloverMinute = null; // an image from before this can not calibrate the new view
            _steps.Clear();
            RestartFrom(knownReached, label);
        }

        if (_label is { } previous)
        {
            double moved = (label - previous).TotalSeconds, passed = Math.Max(0, frameAt - _labelAt);
            if (moved < 0)
            {
                if (!_stopped || moved >= -Jitter) return null; // scrolled back: what is known stays, for when it returns
                WentBack = true;
                _offset = null;
                _rolloverMinute = null;
                _label = label;
                _labelAt = frameAt;
                _stopped = false;
                return null;
            }

            // How far the label can move by itself: what was played, and one step of it
            double possible = passed * speed * 1.2 + Math.Max(Step, 2 * SecondsPerPixel(axis));
            if (moved > possible + JumpAllowance)
            {
                _untrusted = true;
                _offset = null;
                _rolloverMinute = null;
                _steps.Clear();
            }
            else if (moved > possible + ExpectedStepSlack)
            {
                // A little too far: a zoom or move too small to show in the view. Restart from what is known
                _rolloverMinute = null;
                RestartFrom(knownReached, label);
            }
            else if (moved > 0)
            {
                _stopped = false;
                if (passed < 1.5)
                {
                    _steps.Enqueue(moved);
                    if (_steps.Count > 10) _steps.Dequeue();
                }
            }
        }
        _labelAt = frameAt;
        _label = label;
        if (_untrusted) return null;

        if (_rolloverMinute is { } exact && frameAt >= _rolloverAt + CalibrateFrom)
        {
            if (frameAt <= _rolloverAt + CalibrateUntil)
            {
                double offset = (exact - label).TotalSeconds;
                if (offset is >= -CalibrateUntil and <= MaxOffset) _offset = offset;
            }
            _rolloverMinute = null;
        }

        DateTime bound;
        if (canCheckView && _offset is { } known) bound = label.AddSeconds(known - Step);
        else if (axis.BehindDataEnd) bound = label;
        else return null;

        // It can not be past the minute which Deepdom shows
        if (bound >= minute.AddSeconds(62)) return null;
        return bound < minute ? minute : bound;
    }

    // Zoomed far out the label advances in steps of several seconds
    double Step => _steps.Count == 0 ? 1 : Math.Max(1, _steps.Max());

    void RestartFrom(DateTime? knownReached, DateTime label)
    {
        double? offset = knownReached is { } reached ? (reached - label).TotalSeconds : null;
        _offset = offset is > 0 and <= MaxOffset ? offset : null;
    }

    static double SecondsPerPixel(AxisReading a) => a.Spacing > 0 ? (double)a.StepSeconds / a.Spacing : 0;

    static bool ViewChanged(AxisReading a, AxisReading b) =>
        Math.Abs(a.Right - b.Right) > ViewTolerance
        || Math.Abs(a.Spacing - b.Spacing) > SpacingTolerance
        || Math.Abs(a.StepSeconds - b.StepSeconds) > 1;

    static DateTime Nearest(DateTime minute, int secondOfDay)
    {
        var t = minute.Date.AddSeconds(secondOfDay);
        if (t - minute > TimeSpan.FromHours(12)) return t.AddDays(-1);
        if (minute - t > TimeSpan.FromHours(12)) return t.AddDays(1);
        return t;
    }
}

/// <summary>
/// Deepchart's upper bound from its bar countdown.
///
/// With bars of whole minutes the seconds of the countdown give the second within the displayed minute. With
/// shorter bars the result is too late, never too early, which is the safe side.
///
/// - A countdown which stands still says nothing while playing (it could be a stale image): only the moment it
///   changes is used, and the clock extrapolates from there.
/// - While paused the standing value is used, once it had time to settle.
/// - Next to a minute change the minute clock and the countdown can disagree by a minute: those seconds are skipped.
/// </summary>
sealed class DeepchartScreenTime
{
    // The countdown shows 60 - second, rounded up. It is redrawn up to 0.6 s late
    const double RenderDelay = 0.7, SettleTime = 0.6, PausedSlack = 0.2;
    const int TicksToCorrect = 3;

    int? _last;
    double _lastFrameAt;
    int _ticks;
    double? _pausedSince;

    public void Reset()
    {
        _last = null;
        _ticks = 0;
        _pausedSince = null;
    }

    /// <summary>
    /// A time Deepchart has certainly not passed, or null. Ticking: seen counting down several times in a row, so
    /// it may also correct an upper bound which is far too high.
    /// </summary>
    public (DateTime Upper, bool Ticking)? Update(int countdown, double frameAt, double now, DateTime minute, int speed, bool playing)
    {
        bool changed = false;
        if (_last is { } last && countdown != last && frameAt > _lastFrameAt)
        {
            changed = true;
            _ticks = last - countdown is > 0 and <= 3 ? _ticks + 1 : 0;
        }
        _last = countdown;
        _lastFrameAt = frameAt;

        if (playing) _pausedSince = null;
        else _pausedSince ??= now;

        int seconds = countdown % 60;
        if (seconds is <= 1 or >= 57) return null;

        if (playing && changed && _ticks > 0)
        {
            // It just changed to this value: that second of the bar has just begun
            double since = Math.Max(0, now - frameAt) + RenderDelay;
            return (minute.AddSeconds(60 - seconds + since * speed), _ticks >= TicksToCorrect);
        }
        if (!playing && _pausedSince is { } paused && now - paused >= SettleTime)
            return (minute.AddSeconds(61 - seconds + PausedSlack), false);
        return null;
    }
}
