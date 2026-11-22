// ReplaySync: keeps the Deepchart replay from getting ahead of the Deepdom replay.
//
// Deepdom is where the trades happen, so syncing never touches its replay: only Deepchart is paused and
// resumed. The rule is one-sided: Deepchart must never show what Deepdom has not played yet. Deepchart's time is
// taken as an upper bound and Deepdom's as a lower bound, so every uncertainty (no rollover seen yet, Deepdom
// frozen, crashed or paused by hand) holds Deepchart back rather than letting it run ahead.
//
// Usage: dotnet run -- [options]
//
//   --auto              start with the correction on (default: measure only)
//   --margin <s>        pause Deepchart when it is less than this many replay seconds behind Deepdom (default 0.5)
//   --hysteresis <s>    resume it once it is margin + this many seconds behind (default 0.5)
//   --poll <ms>         clock polling interval (default 200). The rollover moment is known to +/- one poll,
//                       which at speed xN is N times as many replay seconds.
//   --freeze-ms <ms>    an app which does not answer within this time counts as frozen (default 500)
//   --catch-up <s>      Forward Deepchart to the minute Deepdom shows when it is more than this far behind (default 90)
//   --status <s>        print a status line every s wall seconds (default 5)
//   --verbose           log every pause and resume
//   --start "<date>"    clean start at this date (same format as the date box, e.g. "06/01/2026 09:30"):
//                       pauses both; Deepchart jumps with Forward when the date is ahead of it, otherwise
//                       Reset + Set; Deepdom always Reset + Set. Waits until both are loaded, then presses Play
//                       in both together.
//   --forward-wait <s>  Forward gives no feedback: wall seconds to wait for it (default 5)
//   --load-timeout <s>  how long to wait for Reset + Set to load (default 180)
//
// Keys:  A = toggle correction   P = play (sync on)   Space = pause both (sync off)   Q = quit
//
// This is only the console front end. The sync itself is SyncController.cs, shared with the desktop app.

using System.Diagnostics;
using System.Globalization;

bool auto = false, verbose = false;
var defaults = new SyncOptions();
double margin = defaults.Margin, hysteresis = defaults.Hysteresis, statusEvery = 5;
double forwardWait = defaults.ForwardWait, loadTimeout = defaults.LoadTimeout;
double catchUp = defaults.CatchUp;
int pollMs = defaults.PollMs, freezeMs = defaults.FreezeMs;
string? startText = null;
for (int i = 0; i < args.Length; i++)
{
    double Num() => double.Parse(args[++i], CultureInfo.InvariantCulture);
    switch (args[i])
    {
        case "--auto": auto = true; break;
        case "--verbose": verbose = true; break;
        case "--margin": margin = Num(); break;
        case "--hysteresis": hysteresis = Num(); break;
        case "--poll": pollMs = (int)Num(); break;
        case "--freeze-ms": freezeMs = (int)Num(); break;
        case "--catch-up": catchUp = Num(); break;
        case "--status": statusEvery = Num(); break;
        case "--start": startText = args[++i]; break;
        case "--forward-wait": forwardWait = Num(); break;
        case "--load-timeout": loadTimeout = Num(); break;
        default: Console.WriteLine($"Unknown argument {args[i]}"); return 1;
    }
}

var options = new SyncOptions
{
    Margin = margin, Hysteresis = hysteresis, PollMs = pollMs, FreezeMs = freezeMs,
    CatchUp = catchUp, ForwardWait = forwardWait, LoadTimeout = loadTimeout, Verbose = verbose,
};
using var sync = new SyncController(options, Log);
sync.SetCorrection(auto);
bool interactive = !Console.IsInputRedirected;

Log($"Margin {margin}s, hysteresis {hysteresis}s, poll {pollMs} ms, freeze after {freezeMs} ms, correction {(auto ? "ON" : "OFF")}.");
if (interactive) Log("Keys: A = toggle correction, P = play (sync on), Space = pause both (sync off), Q = quit");

Console.CancelKeyPress += (_, _) => sync.ReleaseChart("quitting");

if (startText is not null && !sync.CleanStart(startText)) return 2;

var wall = Stopwatch.StartNew();
double nextStatus = 0, heldBefore = 0;
int pausesBefore = 0;

while (true)
{
    sync.Step();

    double now = wall.Elapsed.TotalSeconds;
    if (now >= nextStatus)
    {
        var s = sync.Snapshot();
        PrintStatus(s, s.Pauses - pausesBefore, s.HeldSeconds - heldBefore);
        pausesBefore = s.Pauses;
        heldBefore = s.HeldSeconds;
        nextStatus = now + statusEvery;
    }

    if (interactive)
    {
        while (Console.KeyAvailable)
        {
            switch (Console.ReadKey(true).Key)
            {
                case ConsoleKey.A:
                    sync.SetCorrection(!sync.SyncOn);
                    if (!sync.SyncOn) sync.ReleaseChart("correction off");
                    Log($"Correction {(sync.SyncOn ? "ON" : "OFF")}");
                    break;
                case ConsoleKey.P:
                    sync.Play();
                    break;
                case ConsoleKey.Spacebar:
                    sync.Pause();
                    break;
                case ConsoleKey.Q:
                    sync.ReleaseChart("quitting");
                    return 0;
            }
        }
    }

    Thread.Sleep(pollMs);
}

void PrintStatus(SyncSnapshot s, int pauses, double heldSeconds)
{
    if (s.Chart.Reading is not { } chart || s.Dom.Reading is not { } dom)
    {
        Log("Waiting for both Replay Manage windows (open the replay in Deepchart and Deepdom)...");
        return;
    }

    string drift = s.Ahead is { } ahead ? $"Deepchart ~{SyncController.DescribeGap(ahead)}"
        : s.DomUnconfirmed ? "waiting for Deepdom to confirm its time"
        : "waiting for rollovers";
    string bound = s.AheadBound is { } b ? $" (bound {b:+0.0;-0.0}s)" : "";
    string worst = s.WorstAhead is { } w ? $" | worst at a Deepdom rollover {w:+0.0;-0.0}s, ahead {s.Violations}x" : "";
    string hold = s.SyncOn ? $" | {pauses} pauses, held {Math.Min(100, 100 * heldSeconds / Math.Max(statusEvery, 0.001)):0}%" : " | correction OFF";
    Log($"{Describe(s.Chart, chart)} | {Describe(s.Dom, dom)} | {drift}{bound}{hold}{worst}");
    if (chart.Speed != dom.Speed) Log($"WARNING: speeds differ (Deepchart x{chart.Speed}, Deepdom x{dom.Speed})");
}

static string Describe(AppSnapshot app, ReplayReading r)
{
    string time = app.Anchored ? $"~{app.Estimate:HH:mm:ss}" : $"{app.DisplayedMinute:HH:mm} (no rollover yet)";
    string state = !app.Responding ? "NOT RESPONDING" : r.Playing ? "play" : "paused";
    return $"{app.Label} {time} x{r.Speed} {state} rate {app.Rate:0.00}";
}

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.f}  {message}");
