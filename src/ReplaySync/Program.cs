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
//   --trace             log every change of the date boxes, play states, speeds and screen clocks (measurements)
//   --start "<date>"    clean start at this date (same format as the date box, e.g. "06/01/2026 09:30"):
//                       pauses both; Deepchart jumps with Forward when the date is ahead of it, otherwise
//                       Reset + Set; Deepdom always Reset + Set. Waits until both are loaded, then plays Deepdom.
//                       Deepchart is started by the correction, or right away when that is off.
//   --forward-wait <s>  Forward gives no feedback: wall seconds to wait for it (default 5)
//   --load-timeout <s>  how long to wait for each Reset, Set and load, not counting freezes (default 600)
//   --plan "<steps>"    timed commands for a test run, seconds after the start: "0:play,30:x40,90:x1,180:pause,190:quit"
//
// Keys:  A = toggle correction   P = play (sync on)   Space = pause both (sync off)   Q = quit
//        1 / 2 / 3 / 4 = speed x1 / x8 / x40 / x100 for both (slowing to x1 lets Deepdom pass Deepchart first)
//
// Diagnostic: --speed-test <Deepchart|Deepdom> presses x8, types 5, presses x1 in that app and prints the speed box
// and slider after each step (run it paused).
//
// This is only the console front end. The sync itself is SyncController.cs, shared with the desktop app.

using System.Diagnostics;
using System.Globalization;

if (args.Length == 2 && args[0] == "--speed-test") return SpeedTest(args[1]);

bool auto = false, verbose = false, trace = false;
var defaults = new SyncOptions();
double margin = defaults.Margin, hysteresis = defaults.Hysteresis, statusEvery = 5;
double forwardWait = defaults.ForwardWait, loadTimeout = defaults.LoadTimeout;
double catchUp = defaults.CatchUp;
int pollMs = defaults.PollMs, freezeMs = defaults.FreezeMs;
string? startText = null, planText = null;
for (int i = 0; i < args.Length; i++)
{
    double Num() => double.Parse(args[++i], CultureInfo.InvariantCulture);
    switch (args[i])
    {
        case "--auto": auto = true; break;
        case "--verbose": verbose = true; break;
        case "--trace": trace = true; break;
        case "--margin": margin = Num(); break;
        case "--hysteresis": hysteresis = Num(); break;
        case "--poll": pollMs = (int)Num(); break;
        case "--freeze-ms": freezeMs = (int)Num(); break;
        case "--catch-up": catchUp = Num(); break;
        case "--status": statusEvery = Num(); break;
        case "--start": startText = args[++i]; break;
        case "--forward-wait": forwardWait = Num(); break;
        case "--load-timeout": loadTimeout = Num(); break;
        case "--plan": planText = args[++i]; break;
        default: Console.WriteLine($"Unknown argument {args[i]}"); return 1;
    }
}

var options = new SyncOptions
{
    Margin = margin, Hysteresis = hysteresis, PollMs = pollMs, FreezeMs = freezeMs,
    CatchUp = catchUp, ForwardWait = forwardWait, LoadTimeout = loadTimeout, Verbose = verbose, Trace = trace,
};
using var sync = new SyncController(options, Log);
sync.SetCorrection(auto);
bool interactive = !Console.IsInputRedirected;

Log($"Margin {margin}s, hysteresis {hysteresis}s, poll {pollMs} ms, freeze after {freezeMs} ms, correction {(auto ? "ON" : "OFF")}.");
if (interactive) Log("Keys: A = toggle correction, P = play (sync on), Space = pause both (sync off), 1-4 = x1/x8/x40/x100, Q = quit");

Console.CancelKeyPress += (_, _) => sync.ReleaseChart("quitting");

if (startText is not null && !sync.CleanStart(startText, playChart: !auto)) return 2;

var plan = new Queue<(double At, string Command)>((planText ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(step => step.Split(':'))
    .Select(p => (double.Parse(p[0], CultureInfo.InvariantCulture), p[1].Trim().ToLowerInvariant()))
    .OrderBy(p => p.Item1));
var wall = Stopwatch.StartNew();
double nextStatus = 0, heldBefore = 0;
int pausesBefore = 0;

while (true)
{
    sync.Step();

    while (plan.TryPeek(out var step) && wall.Elapsed.TotalSeconds >= step.At)
    {
        plan.Dequeue();
        Log($"Plan: {step.Command}");
        switch (step.Command)
        {
            case "play": sync.Play(); break;
            case "pause": sync.Pause(); break;
            case "quit": return 0;
            case var x when x.StartsWith('x') && int.TryParse(x[1..], out int speed): sync.RequestSpeed(speed); break;
            default: Log($"Plan: unknown command {step.Command}"); break;
        }
    }

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
                case ConsoleKey.D1: sync.RequestSpeed(1); break;
                case ConsoleKey.D2: sync.RequestSpeed(8); break;
                case ConsoleKey.D3: sync.RequestSpeed(40); break;
                case ConsoleKey.D4: sync.RequestSpeed(100); break;
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

    string drift = s.FastForward ? $"fast forward, Deepchart ~{(s.FastAhead is { } f ? SyncController.DescribeGap(f) : "unknown")} (band +/-{s.FastBand / 2:0}s)"
        : s.DomChartStuck ? "Deepdom's chart is not moving"
        : s.Ahead is { } ahead ? $"Deepchart ~{SyncController.DescribeGap(ahead)}"
        : s.DomUnconfirmed ? "waiting for Deepdom to confirm its time"
        : "waiting for rollovers";
    string bound = s.AheadBound is { } b ? $" (bound {b:+0.0;-0.0}s)" : "";
    string worst = s.WorstAhead is { } w ? $" | worst at a Deepdom rollover {w:+0.0;-0.0}s, ahead {s.Violations}x" : "";
    string hold = s.SyncOn ? $" | {pauses} pauses, held {Math.Min(100, 100 * heldSeconds / Math.Max(statusEvery, 0.001)):0}%" : " | correction OFF";
    Log($"{Describe(s.Chart, chart)} | {Describe(s.Dom, dom)} | {drift}{bound}{hold}{worst}");
    if (chart.Speed != dom.Speed && s.SpeedTarget is null) Log($"WARNING: speeds differ (Deepchart x{chart.Speed}, Deepdom x{dom.Speed})");
}

static string Describe(AppSnapshot app, ReplayReading r)
{
    string time = app.Anchored ? $"~{app.Estimate:HH:mm:ss}" : $"{app.DisplayedMinute:HH:mm} (no rollover yet)";
    string state = !app.Responding ? "NOT RESPONDING" : r.Playing ? "play" : "paused";
    return $"{app.Label} {time} x{r.Speed} {state} rate {app.Rate:0.00}";
}

// One-shot check of the speed controls on one app (paused): preset x8, typed 5, preset x1. Prints the speed box and
// the slider after each step: a slider which follows means the app took the value.
static int SpeedTest(string process)
{
    using var automation = new FlaUI.UIA3.UIA3Automation
    {
        ConnectionTimeout = TimeSpan.FromMilliseconds(2000),
        TransactionTimeout = TimeSpan.FromMilliseconds(2000),
    };
    automation.NativeAutomation2.AutoSetFocus = 0;
    var app = new ReplayApp(process, process, automation);
    int attempts = 0;
    while (!app.TryConnect())
    {
        if (++attempts == 5) { Log($"{process}: no Replay Manage window"); return 1; }
        Thread.Sleep(2000);
    }

    var window = automation.GetDesktop().FindAllChildren().First(w =>
        w.Properties.Name.ValueOrDefault == "Replay Manage" && w.Properties.ProcessId.ValueOrDefault == app.ProcessId);
    var slider = window.FindFirstDescendant(automation.ConditionFactory.ByAutomationId("tkbSpeed"));
    void Show(string after)
    {
        Thread.Sleep(1000);
        string sliderValue = "?";
        try { sliderValue = slider?.Patterns.RangeValue.Pattern.Value.Value.ToString(CultureInfo.InvariantCulture) ?? "none"; } catch { }
        string box = app.TryRead(out var r) ? $"box x{r.Speed} {(r.Playing ? "PLAYING" : "paused")}" : "no reading";
        Log($"{process} after {after}: {box}, slider {sliderValue}");
    }

    Show("nothing");
    Log($"press x8: {app.PressSpeedPreset(8)}");
    Show("x8");
    Log($"type 5: {app.TypeSpeed(5)}");
    Show("typing 5");
    Thread.Sleep(2000);
    Show("3 s more");
    Log($"press x1: {app.PressSpeedPreset(1)}");
    Show("x1");
    return 0;
}

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.f}  {message}");
