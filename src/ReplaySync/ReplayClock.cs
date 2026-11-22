/// <summary>
/// The replay time of one app with sub-minute precision.
///
/// The moment the minute display rolls over from 09:18 to 09:19 the replay is at 09:19:00: that is an exact anchor.
/// The clocks read from the screen (see Screen/ScreenTime.cs) tighten it in between. Without those the time is
/// extrapolated from the playing wall time, the speed setting and the app's rate.
///
/// Besides the best guess (Estimate) the clock keeps a conservative Bound: an upper bound for the app which must
/// not get ahead (Deepchart), a lower bound for the app it follows (Deepdom). Before the first anchor the bound is
/// the worst case the display allows: the end of the displayed minute, or its start.
///
/// The lower bound is never extrapolated, it moves only with what was seen. No assumed rate is safe for
/// it: Deepdom was measured at 0.86 and at 0.57 of its speed on the same day, and it stalls. The rate only moves
/// the best guess.
/// </summary>
sealed class ReplayClock(bool upperBound, double initialRate)
{
    // Deepchart was measured at 0.93 to 1.01 of its speed
    const double FastestPlayback = 1.02;
    // Without ticks to confirm it, a screen reading may lower the upper bound only this far
    const double MaxCorrection = 3;
    const double ScreenSlack = 1;

    public DateTime? DisplayedMinute { get; private set; }
    /// <summary>Best guess of the replay time (meaningful when Anchored).</summary>
    public DateTime Estimate { get; private set; }
    /// <summary>True after an exact anchor or a screen reading, since the last jump.</summary>
    public bool Anchored { get; private set; }
    /// <summary>Replay seconds per nominal replay second (speed x wall seconds). 1 = keeps up with its speed.</summary>
    public double Rate { get; private set; } = initialRate;

    DateTime _bound;
    double _nominalSinceAnchor; // replay seconds the app should have played since the minute anchor at its speed setting
    bool _minuteAnchored;       // the rate is only learned from one minute anchor to the next
    int _rateSamples;

    /// <summary>False while Rate is still the assumed one.</summary>
    public bool RateMeasured => _rateSamples > 0;

    /// <summary>Conservative replay time: never below the real one for an upper bound clock, never above for a lower.</summary>
    public DateTime Bound => Anchored ? _bound
        : upperBound ? DisplayedMinute!.Value.AddSeconds(59.9) : DisplayedMinute!.Value;

    /// <summary>
    /// Feeds one reading. wallSeconds is the wall time since the previous reading. With credit false (the app did
    /// not answer, or its time is being read from the screen) that time counts for learning the rate but does not
    /// move the estimate forward. Returns true when this reading is a rollover (an exact anchor).
    /// </summary>
    public bool Update(ReplayReading r, double wallSeconds, bool credit = true)
    {
        if (r.Minute is not { } minute) return false;

        if (DisplayedMinute is null)
        {
            Jump(minute);
            return false;
        }

        if (minute == DisplayedMinute)
        {
            if (r.Playing)
            {
                double nominal = wallSeconds * r.Speed;
                _nominalSinceAnchor += nominal;
                if (credit)
                {
                    var max = minute.AddSeconds(59.9);
                    Estimate = Min(Estimate.AddSeconds(nominal * Rate), max);
                    if (upperBound) _bound = Min(_bound.AddSeconds(nominal * FastestPlayback), max);
                }
            }
            return false;
        }

        if (minute == DisplayedMinute.Value.AddMinutes(1))
        {
            // Rollover. The rate is learned only between two consecutive anchors with no jump in between.
            if (_minuteAnchored && _nominalSinceAnchor > 1)
            {
                double measured = Math.Clamp(60.0 / _nominalSinceAnchor, 0.02, 3.0);
                Rate = _rateSamples++ == 0 ? measured : 0.7 * Rate + 0.3 * measured;
            }
            AnchorAt(minute);
            return true;
        }

        // Any other change: Set, Forward, Reset, or minutes skipped (very high speed). Start over.
        Jump(minute);
        return false;
    }

    /// <summary>The replay is known to be exactly at this minute (e.g. Play pressed right after Set).</summary>
    public void AnchorAt(DateTime minute)
    {
        DisplayedMinute = minute;
        Estimate = _bound = minute;
        Anchored = _minuteAnchored = true;
        _nominalSinceAnchor = 0;
    }

    /// <summary>Lower bound clock: the replay has certainly reached this time.</summary>
    public void Reached(DateTime time)
    {
        if (DisplayedMinute is not { } minute) return;
        time = Min(Max(time, minute), minute.AddSeconds(59.9));
        _bound = Anchored ? Max(_bound, time) : time;
        // The bound is the pessimistic edge: rounding and the age of the image put the replay about this far past it
        Estimate = Min(_bound.AddSeconds(ScreenSlack), minute.AddSeconds(59.9));
        Anchored = true;
    }

    /// <summary>
    /// Upper bound clock: the replay has certainly not passed this time. A reading far below the bound is only
    /// believed when confirmed (the countdown is ticking), otherwise it could be stale or typed text.
    /// </summary>
    public void NotPast(DateTime time, bool confirmed)
    {
        if (DisplayedMinute is not { } minute) return;
        time = Min(Max(time, minute), minute.AddSeconds(59.9));
        if (Anchored && !confirmed && time < _bound.AddSeconds(-MaxCorrection)) return;
        _bound = time;
        Estimate = Max(minute, time.AddSeconds(-ScreenSlack));
        Anchored = true;
    }

    /// <summary>Forget what is known within the displayed minute.</summary>
    public void Restart()
    {
        if (DisplayedMinute is { } minute) Jump(minute);
    }

    void Jump(DateTime minute)
    {
        DisplayedMinute = minute;
        Estimate = _bound = minute;
        Anchored = _minuteAnchored = false;
        _nominalSinceAnchor = 0;
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
