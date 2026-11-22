namespace DeepReplaySync;

enum Level { Idle, Good, Holding, Danger }

/// <summary>
/// The one sentence at the top of the window. The order of the checks matters: the first one which applies wins, so
/// everything which means "not guarded" comes before anything reassuring.
/// </summary>
readonly record struct Banner(Level Level, string Title, string Detail)
{
    // A worst case below this is within what the clocks can resolve
    const double ClearlyAhead = 2;

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

        // While Deepdom's time is only a rough lower bound, "ahead" would mostly be a false alarm
        if (s is { Ahead: > 0, AheadBound: > ClearlyAhead, DomTimeIsRough: false })
        {
            return new(Level.Danger, "Deepchart is ahead", chartPlaying
                ? "It is playing past what Deepdom has played."
                : "It is paused. Don't read the chart until Deepdom has passed it.");
        }

        if (!s.SyncOn)
        {
            if (chartPlaying)
                return new(Level.Danger, "Not in sync", "Deepchart is playing by itself. Press Play to sync both.");
            if (domPlaying)
                return new(Level.Holding, "Not in sync", "Deepdom is playing by itself. Press Play to sync both.");
            return new(Level.Idle, "Paused", "Play runs both replays in sync.");
        }

        string state = chartPlaying ? "Pausing Deepchart" : "Holding Deepchart";
        if (!dom.Connected)
            return new(Level.Holding, state, "Deepdom's Replay Manage window was not found.");
        if (!dom.Responding)
            return new(Level.Holding, chartPlaying ? "Deepdom is not responding" : state, "Deepdom's time is not credited until it answers.");
        if (s.DomUnconfirmed)
            return new(Level.Holding, state, "Deepdom's time changed. Waiting for its next minute to confirm it.");
        if (domPlaying && !dom.ToTheSecond)
            return new(Level.Holding, state, "Can not read the seconds in Deepdom. Keep its chart and time axis visible.");
        if (chartPlaying)
            return new(Level.Good, "Playing in sync", "Deepchart follows just behind Deepdom.");
        if (!domPlaying)
            return new(Level.Holding, "Deepdom is paused", "Deepchart waits. Play continues both.");
        if (!chart.Anchored || !dom.Anchored)
            return new(Level.Holding, state, "The exact times are known at each app's next minute change.");
        if (s.AheadBound is null or > ClearlyAhead)
            return new(Level.Holding, state, "Waiting until Deepdom has certainly passed it.");
        return new(Level.Good, "Playing in sync", "Deepchart follows just behind Deepdom.");
    }
}
