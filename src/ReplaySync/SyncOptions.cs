/// <summary>The tuning of a <see cref="SyncController"/>. The README of ReplaySync explains each value.</summary>
sealed class SyncOptions
{
    /// <summary>Pause Deepchart when it is less than this many replay seconds behind Deepdom.</summary>
    public double Margin { get; set; } = 0.5;
    /// <summary>Resume it once it is Margin + this behind.</summary>
    public double Hysteresis { get; set; } = 0.5;
    public int PollMs { get; init; } = 200;
    /// <summary>An app which does not answer within this time counts as frozen.</summary>
    public int FreezeMs { get; init; } = 500;
    /// <summary>Forward Deepchart when it is further behind than this.</summary>
    public double CatchUp { get; set; } = 90;
    /// <summary>Forward gives no feedback: wall seconds to wait for it.</summary>
    public double ForwardWait { get; init; } = 5;
    /// <summary>Wall seconds to wait for Reset + Set to load.</summary>
    public double LoadTimeout { get; init; } = 180;
    /// <summary>Log every pause and resume.</summary>
    public bool Verbose { get; init; }
}
