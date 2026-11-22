using System.Text.Json;

namespace DeepReplaySync;

/// <summary>What the app remembers between runs. Whether the sync is on is deliberately not remembered.</summary>
sealed class Settings
{
    static readonly SyncOptions Defaults = new();
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepReplaySync", "settings.json");

    public double Margin { get; set; } = Defaults.Margin;
    public double Hysteresis { get; set; } = Defaults.Hysteresis;
    public double DomSafety { get; set; } = Defaults.DomSafety;
    public double CatchUp { get; set; } = Defaults.CatchUp;
    public bool AlwaysOnTop { get; set; }

    // A value outside these could let Deepchart get ahead
    public static bool ValidMargin(double v) => v is >= 0 and <= 60;
    public static bool ValidHysteresis(double v) => v is >= 0 and <= 60;
    public static bool ValidSafety(double v) => v is >= 0.1 and <= 1;
    public static bool ValidCatchUp(double v) => v is >= 30 and <= 86400;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) is { } s
                && ValidMargin(s.Margin) && ValidHysteresis(s.Hysteresis) && ValidSafety(s.DomSafety) && ValidCatchUp(s.CatchUp))
            {
                return s;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // unreadable: the defaults are the safe choice
        }
        return new Settings();
    }

    /// <summary>Returns what failed, or null.</summary>
    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    public SyncOptions ToOptions() => new()
    {
        Margin = Margin,
        Hysteresis = Hysteresis,
        DomSafety = DomSafety,
        CatchUp = CatchUp,
    };
}
