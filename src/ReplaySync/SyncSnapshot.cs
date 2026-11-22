/// <summary>One app as the controller last saw it. Immutable, so a UI thread can read it.</summary>
sealed record AppSnapshot(
    string Label,
    bool Connected,
    bool Responding,
    ReplayReading? Reading,
    DateTime? DisplayedMinute,
    DateTime Estimate,
    bool Anchored,
    double Rate,
    bool RateMeasured,
    bool ToTheSecond);

/// <summary>
/// Everything a front end shows. Ahead and AheadBound are replay seconds, positive when Deepchart is ahead of Deepdom:
/// the best guess and the worst case. Either is null while it is not known.
/// </summary>
sealed record SyncSnapshot(
    AppSnapshot Chart,
    AppSnapshot Dom,
    bool SyncOn,
    bool ChartHeld,
    bool DomUnconfirmed,
    bool ChartReset,
    bool DomTimeIsRough,
    double? Ahead,
    double? AheadBound,
    int Pauses,
    double HeldSeconds,
    double? WorstAhead,
    int Violations,
    string? Activity);
