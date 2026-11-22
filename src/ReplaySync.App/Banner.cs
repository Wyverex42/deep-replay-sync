namespace DeepReplaySync;

enum Level { Idle, Good, Holding, Danger }

/// <summary>
/// The one sentence at the top of the window. The order of the checks matters: the first one which applies wins, so
/// everything which means "not guarded" comes before anything reassuring.
/// </summary>
readonly record struct Banner(Level Level, string Title, string Detail)
{
    // A worst case below this is within what the rollover timing can resolve
    const double ClearlyAhead = 0.5;

    public static Banner For(SyncSnapshot s)
    {
        var chart = s.Chart;
        var dom = s.Dom;
        bool chartPlaying = chart.Reading is { Playing: true };
        bool domPlaying = dom.Reading is { Playing: true };

        if (s.Activity is { } activity)
            return new(Level.Holding, "Working", activity);

        if (!chart.Connected)
        {
            return new(s.SyncOn ? Level.Danger : Level.Idle, "Deepchart not found",
                "Open its Replay Manage window. If Deepchart runs as administrator, so must this app.");
        }
        if (!chart.Responding)
            return new(s.SyncOn ? Level.Danger : Level.Idle, "Deepchart is not responding", "It can not be paused until it answers.");

        if (s is { Ahead: > 0, AheadBound: > ClearlyAhead })
        {
            return new(Level.Danger, "Deepchart is ahead", chartPlaying
                ? "It is playing past what Deepdom has played."
                : "It is paused. Don't read the chart until Deepdom has passed it.");
        }

        if (!s.SyncOn)
        {
            return chartPlaying
                ? new(Level.Danger, "Sync is off", "Deepchart is playing and nothing holds it back.")
                : new(Level.Idle, "Sync is off", "Only measuring. Nothing is pressed in either app.");
        }

        string state = chartPlaying ? "Pausing Deepchart" : "Holding Deepchart";
        if (!dom.Connected)
            return new(Level.Holding, state, "Deepdom's Replay Manage window was not found.");
        if (!dom.Responding)
            return new(Level.Holding, chartPlaying ? "Deepdom is not responding" : state, "Deepdom's time is not credited until it answers.");
        if (!chartPlaying && !s.ChartHeld)
            return new(Level.Holding, "Deepchart is paused", "Not paused by the sync. Let it follow to resume it when that is safe.");
        if (s.DomUnconfirmed)
            return new(Level.Holding, state, "Deepdom's time changed. Waiting for its next minute to confirm it.");
        if (chartPlaying)
            return new(Level.Good, "Following Deepdom", "Deepchart plays while it is safely behind.");
        if (!domPlaying)
            return new(Level.Holding, state, "Deepdom is paused. Play it in Deepdom.");
        if (!chart.Anchored || !dom.Anchored)
            return new(Level.Holding, state, "The exact times are known at each app's next minute change.");
        if (s.AheadBound is null or > 0)
            return new(Level.Holding, state, "It may be ahead. Waiting until Deepdom has certainly passed it.");
        return new(Level.Good, "Following Deepdom", "Short pause to stay behind.");
    }
}
