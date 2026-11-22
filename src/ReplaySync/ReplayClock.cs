/// <summary>
/// Estimates the replay time of one app with sub-minute precision from a display which shows only minutes.
///
/// The moment the display rolls over from 09:18 to 09:19 the replay is at 09:19:00: that is an exact anchor.
/// Between anchors the time is extrapolated from the playing wall time, the speed setting and the effective rate
/// learned from consecutive rollovers (an app which can not keep up plays slower than its speed setting).
///
/// Besides the best guess (Estimate) the clock keeps a conservative Bound: an upper bound for the app which must
/// not get ahead (Deepchart), a lower bound for the app it follows (Deepdom). Before the first anchor the bound is
/// the worst case the display allows: the end of the displayed minute, or its start.
/// </summary>
sealed class ReplayClock(bool upperBound, double initialRate, double bias)
{
    public DateTime? DisplayedMinute { get; private set; }
    /// <summary>Best guess of the replay time (meaningful when Anchored).</summary>
    public DateTime Estimate { get; private set; }
    /// <summary>True after an exact anchor since the last jump.</summary>
    public bool Anchored { get; private set; }
    /// <summary>Replay seconds per nominal replay second (speed x wall seconds). 1 = keeps up with its speed.</summary>
    public double Rate { get; private set; } = initialRate;

    DateTime _bound;
    double _nominalSinceAnchor; // replay seconds the app should have played since the anchor at its speed setting
    int _rateSamples;

    /// <summary>Conservative replay time: never below the real one for an upper bound clock, never above for a lower.</summary>
    public DateTime Bound => Anchored ? _bound
        : upperBound ? DisplayedMinute!.Value.AddSeconds(59.9) : DisplayedMinute!.Value;

    /// <summary>
    /// Feeds one reading. wallSeconds is the wall time since the previous reading. With credit false (the app did
    /// not answer for a while) that time counts for learning the rate but does not move the estimate forward.
    /// Returns true when this reading is a rollover (an exact anchor).
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
                    _bound = Min(_bound.AddSeconds(nominal * Rate * bias), max);
                }
            }
            return false;
        }

        if (minute == DisplayedMinute.Value.AddMinutes(1))
        {
            // Rollover. The rate is learned only between two consecutive anchors with no jump in between.
            if (Anchored && _nominalSinceAnchor > 1)
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
        Anchored = true;
        _nominalSinceAnchor = 0;
    }

    void Jump(DateTime minute)
    {
        DisplayedMinute = minute;
        Estimate = _bound = minute;
        Anchored = false;
        _nominalSinceAnchor = 0;
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
