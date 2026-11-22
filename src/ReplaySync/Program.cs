// ReplaySync: keeps the Deepchart replay from getting ahead of the Deepdom replay.
//
// Deepdom is where the trades happen, so its replay is never touched while syncing: only Deepchart is paused and
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
//   --dom-rate <r>      Deepdom's rate before it is measured (default 0.6: it plays slower than its speed setting)
//   --dom-safety <f>    Deepdom's lower bound advances at its measured rate times this factor (default 0.8)
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
// Keys:  A = toggle correction   P = play both   Space = pause both   Q = quit

using System.Diagnostics;
using System.Globalization;
using FlaUI.UIA3;

bool auto = false, verbose = false;
double margin = 0.5, hysteresis = 0.5, statusEvery = 5, forwardWait = 5, loadTimeout = 180;
double domRate = 0.6, domSafety = 0.8, catchUp = 90;
int pollMs = 200, freezeMs = 500;
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
        case "--dom-rate": domRate = Num(); break;
        case "--dom-safety": domSafety = Num(); break;
        case "--catch-up": catchUp = Num(); break;
        case "--status": statusEvery = Num(); break;
        case "--start": startText = args[++i]; break;
        case "--forward-wait": forwardWait = Num(); break;
        case "--load-timeout": loadTimeout = Num(); break;
        default: Console.WriteLine($"Unknown argument {args[i]}"); return 1;
    }
}

using var automation = new UIA3Automation();
// A frozen app must not block the loop: Deepchart keeps playing while we wait for Deepdom
automation.ConnectionTimeout = TimeSpan.FromMilliseconds(freezeMs);
automation.TransactionTimeout = TimeSpan.FromMilliseconds(freezeMs);

var chart = new ReplayApp("Deepchart", "Deepchart", automation);
var dom = new ReplayApp("Deepdom", "Deepdom", automation);
var chartClock = new ReplayClock(upperBound: true, initialRate: 1.0, bias: 1.05);
var domClock = new ReplayClock(upperBound: false, initialRate: domRate, bias: domSafety);
var clocks = new Dictionary<ReplayApp, ReplayClock> { [chart] = chartClock, [dom] = domClock };
var last = new Dictionary<ReplayApp, ReplayReading>();
var lastAttempt = new Dictionary<ReplayApp, double>();
var lastOk = new Dictionary<ReplayApp, bool> { [chart] = false, [dom] = false };
var unresponsiveSince = new Dictionary<ReplayApp, double?> { [chart] = null, [dom] = null };

bool chartHeld = false;           // Deepchart paused by this tool
int holds = 0;                    // pauses in the current status interval
double heldTime = 0;              // wall seconds held in the current status interval
double worstAhead = double.NegativeInfinity; // worst real lead of Deepchart seen at a Deepdom rollover
int violations = 0;               // Deepdom rollovers at which Deepchart was really ahead
bool interactive = !Console.IsInputRedirected;

Log($"Margin {margin}s, hysteresis {hysteresis}s, poll {pollMs} ms, freeze after {freezeMs} ms, correction {(auto ? "ON" : "OFF")}.");
if (interactive) Log("Keys: A = toggle correction, P = play both, Space = pause both, Q = quit");

Console.CancelKeyPress += (_, _) => ReleaseChart("quitting");

if (startText is not null && !CleanStart(startText)) return 2;

var wall = Stopwatch.StartNew();
double nextStatus = 0, nextConnect = 0, prevNow = 0;

while (true)
{
    double now = wall.Elapsed.TotalSeconds;
    if (chartHeld) heldTime += now - prevNow;
    prevNow = now;

    // Connect / reconnect
    if (now >= nextConnect && (!chart.IsConnected || !dom.IsConnected))
    {
        foreach (var app in new[] { chart, dom })
        {
            if (!app.IsConnected && app.TryConnect())
            {
                Log($"{app.Label}: connected to the Replay Manage window");
                lastOk[app] = false;
            }
        }
        nextConnect = now + 2;
    }

    // Read Deepdom first: it is the reference
    foreach (var app in new[] { dom, chart })
    {
        if (!app.IsConnected) continue;
        double dt = lastAttempt.TryGetValue(app, out var prev) ? now - prev : 0;
        lastAttempt[app] = now;

        var status = app.Read(out var r);
        if (status != ReadStatus.Ok)
        {
            if (status == ReadStatus.Gone) Log($"{app.Label}: Replay Manage window gone{(app.IsAlive ? "" : " (process exited)")}");
            else if (unresponsiveSince[app] is null) { unresponsiveSince[app] = now; Log($"{app.Label}: not responding"); }
            lastOk[app] = false;
            continue;
        }
        if (unresponsiveSince[app] is { } since)
        {
            Log($"{app.Label}: responding again after {now - since:0.0}s");
            unresponsiveSince[app] = null;
        }

        // Time while Deepdom did not answer is not credited: it may have stalled. Deepchart is always credited.
        bool credit = lastOk[app] || app == chart;
        lastOk[app] = true;
        last[app] = r;

        var clock = clocks[app];
        var before = clock.DisplayedMinute;
        if (clock.Update(r, dt, credit))
        {
            if (app == dom && chartClock.Anchored)
            {
                // Deepdom is exactly at the minute: how far ahead is Deepchart really (best guess, not bound)?
                double ahead = (chartClock.Estimate - domClock.Estimate).TotalSeconds;
                worstAhead = Math.Max(worstAhead, ahead);
                if (ahead > 0) violations++;
                Log($"Deepdom at {domClock.Estimate:HH:mm:ss} (exact), Deepchart ~{Fmt(ahead)}  [Deepdom rate {domClock.Rate:0.00}]");
            }
            else if (app == chart && domClock.Anchored)
            {
                double ahead = (chartClock.Estimate - domClock.Estimate).TotalSeconds;
                Log($"Deepchart at {chartClock.Estimate:HH:mm:ss} (exact), ~{Fmt(ahead)}  [Deepchart rate {chartClock.Rate:0.00}]");
            }
        }
        else if (before is not null && clock.DisplayedMinute != before)
        {
            Log($"{app.Label}: replay time jumped {before:HH:mm} -> {clock.DisplayedMinute:HH:mm}");
        }
    }

    // Correction: only Deepchart is ever paused or resumed
    if (auto && chart.IsConnected && last.ContainsKey(chart) && chartClock.DisplayedMinute is not null)
    {
        // Deepchart's upper bound against Deepdom's lower bound. Deepdom never seen yet: hold.
        double? aheadBound = domClock.DisplayedMinute is null ? null : (chartClock.Bound - domClock.Bound).TotalSeconds;
        bool mustHold = aheadBound is null || aheadBound > -margin;
        bool mayResume = aheadBound is { } a && a <= -(margin + hysteresis);
        bool chartPlaying = last[chart].Playing;

        // Far behind (restart, Deepdom ahead after a Set): jump Deepchart to the minute Deepdom shows. Safe: Deepdom
        // is at or past its displayed minute. Forward only goes ahead, so this never moves Deepchart back.
        if (aheadBound is { } far && far < -catchUp && chart.CanForward && last.ContainsKey(dom)
            && domClock.DisplayedMinute > chartClock.DisplayedMinute)
        {
            var targetMinute = domClock.DisplayedMinute!.Value;
            Log($"Deepchart is {-far:0}s behind: Forward to {targetMinute:HH:mm}");
            if (chart.EnsurePlaying(false, 10))
            {
                chartHeld = true;
                if (chart.ForwardTo(last[dom].Text) is { } error) Log($"Forward failed: {error}");
                else
                {
                    Thread.Sleep(TimeSpan.FromSeconds(forwardWait));
                    chartClock.AnchorAt(targetMinute);
                    last[chart] = last[chart] with { Playing = false, Minute = targetMinute };
                    lastAttempt[chart] = wall.Elapsed.TotalSeconds;
                }
            }
            Thread.Sleep(pollMs);
            continue;
        }

        if (chartPlaying && mustHold)
        {
            if (chart.Pause())
            {
                if (!chartHeld) holds++;
                if (verbose || chartHeld) Log(chartHeld ? "Deepchart was resumed by hand while ahead: paused again" : $"pause Deepchart ({aheadBound:0.0}s)");
                chartHeld = true;
                // Seen as paused from now on, without waiting for the next read
                last[chart] = last[chart] with { Playing = false };
            }
        }
        else if (chartHeld && !chartPlaying && mayResume)
        {
            if (chart.Play())
            {
                if (verbose) Log($"resume Deepchart ({aheadBound:0.0}s)");
                chartHeld = false;
                last[chart] = last[chart] with { Playing = true };
            }
        }
    }

    // Status
    if (now >= nextStatus)
    {
        if (last.ContainsKey(chart) && last.ContainsKey(dom))
        {
            string drift = chartClock.Anchored && domClock.Anchored
                ? $"Deepchart ~{Fmt((chartClock.Estimate - domClock.Estimate).TotalSeconds)}"
                : "waiting for rollovers";
            string bound = domClock.DisplayedMinute is not null && chartClock.DisplayedMinute is not null
                ? $" (bound {(chartClock.Bound - domClock.Bound).TotalSeconds:+0.0;-0.0}s)" : "";
            string worst = violations > 0 || worstAhead > double.NegativeInfinity
                ? $" | worst at a Deepdom rollover {worstAhead:+0.0;-0.0}s, ahead {violations}x" : "";
            string hold = auto ? $" | {holds} pauses, held {100 * heldTime / Math.Max(statusEvery, 0.001):0}%" : " | correction OFF";
            Log($"{Describe(chart)} | {Describe(dom)} | {drift}{bound}{hold}{worst}");
            if (last[chart].Speed != last[dom].Speed) Log($"WARNING: speeds differ (Deepchart x{last[chart].Speed}, Deepdom x{last[dom].Speed})");
        }
        else
        {
            Log("Waiting for both Replay Manage windows (open the replay in Deepchart and Deepdom)...");
        }
        holds = 0;
        heldTime = 0;
        nextStatus = now + statusEvery;
    }

    // Keys
    if (interactive)
    {
        while (Console.KeyAvailable)
        {
            switch (Console.ReadKey(true).Key)
            {
                case ConsoleKey.A:
                    auto = !auto;
                    if (!auto) ReleaseChart("correction off");
                    Log($"Correction {(auto ? "ON" : "OFF")}");
                    break;
                case ConsoleKey.P:
                    // Deepdom first; with the correction on Deepchart is resumed only when it is safely behind
                    dom.Play();
                    if (!auto) chart.Play();
                    Log("Play");
                    break;
                case ConsoleKey.Spacebar:
                    chart.Pause(); dom.Pause();
                    chartHeld = false;
                    Log("Pause both");
                    break;
                case ConsoleKey.Q:
                    ReleaseChart("quitting");
                    return 0;
            }
        }
    }

    Thread.Sleep(pollMs);
}

void ReleaseChart(string why)
{
    if (chartHeld && chart.Play()) Log($"Deepchart resumed ({why})");
    chartHeld = false;
}

bool CleanStart(string text)
{
    var apps = new[] { chart, dom };
    var deadline = Stopwatch.StartNew();
    while (!apps.All(a => a.IsConnected || a.TryConnect()))
    {
        if (deadline.Elapsed.TotalSeconds > 30) { Log("Clean start: both Replay Manage windows must be open"); return false; }
        Thread.Sleep(500);
    }

    foreach (var app in apps)
    {
        if (app.TryRead(out var r) && r.Playing) app.Pause();
    }
    Thread.Sleep(300);

    if (ReplayApp.ParseMinute(text) is not { } target) { Log($"Clean start: can not read the date \"{text}\""); return false; }

    // Deepdom first: Reset + Set is the slow and fragile part
    if (dom.ResetAndSet(text) is { } domError) { Log($"Clean start: Deepdom: {domError}"); return false; }
    Log($"Clean start: Deepdom: Reset + Set to {text}, loading...");

    // Deepchart: Forward when the target is ahead (fast, keeps the loaded data), otherwise Reset + Set
    bool forwarded = false;
    if (chart.CanForward && chart.TryRead(out var cr) && cr.Minute is { } chartMinute && target > chartMinute)
    {
        if (chart.ForwardTo(text) is { } fwdError) { Log($"Clean start: Deepchart: {fwdError}"); return false; }
        forwarded = true;
        Log($"Clean start: Deepchart: Forward from {chartMinute:HH:mm} to {target:HH:mm}");
    }
    else
    {
        if (chart.ResetAndSet(text) is { } chartError) { Log($"Clean start: Deepchart: {chartError}"); return false; }
        Log($"Clean start: Deepchart: Reset + Set to {text}, loading...");
    }

    // Wait until both are loaded (Play enabled). Deepdom sometimes crashes here: notice it instead of hanging.
    deadline.Restart();
    while (!(dom.CanPlay && chart.CanPlay))
    {
        foreach (var app in apps)
        {
            if (!app.IsAlive) { Log($"Clean start: {app.Label} exited (crashed?) while loading. Restart it and run again."); return false; }
        }
        if (deadline.Elapsed.TotalSeconds > loadTimeout)
        {
            Log($"Clean start: timed out after {loadTimeout:0}s (Deepchart {(chart.CanPlay ? "ready" : "loading")}, Deepdom {(dom.CanPlay ? "ready" : "loading")})");
            return false;
        }
        Thread.Sleep(200);
    }
    Log($"Clean start: both loaded after {deadline.Elapsed.TotalSeconds:0.0}s");

    // Forward has no feedback: give it the configured time, counted from the Forward press
    if (forwarded && deadline.Elapsed.TotalSeconds < forwardWait)
        Thread.Sleep(TimeSpan.FromSeconds(forwardWait - deadline.Elapsed.TotalSeconds));

    // Both at the same whole minute: Play in Deepdom first, so Deepchart can only start after it.
    // Both clocks are then exactly at the start minute: anchor them, no waiting for a first rollover.
    bool domOk = dom.EnsurePlaying(true, 30), chartOk = domOk && chart.EnsurePlaying(true, 30);
    Log($"Clean start: Play pressed (Deepdom {(domOk ? "ok" : "FAILED")}, Deepchart {(chartOk ? "ok" : "FAILED")})");
    if (!domOk || !chartOk) return false;
    domClock.AnchorAt(target);
    chartClock.AnchorAt(target);
    return true;
}

string Describe(ReplayApp app)
{
    var r = last[app];
    var c = clocks[app];
    string time = c.Anchored ? $"~{c.Estimate:HH:mm:ss}" : $"{c.DisplayedMinute:HH:mm} (no rollover yet)";
    string state = unresponsiveSince[app] is not null ? "NOT RESPONDING" : r.Playing ? "play" : "paused";
    return $"{app.Label} {time} x{r.Speed} {state} rate {c.Rate:0.00}";
}

static string Fmt(double seconds) => seconds switch
{
    > 0 => $"{seconds:0.0}s AHEAD of Deepdom",
    < 0 => $"{-seconds:0.0}s behind Deepdom",
    _ => "level with Deepdom",
};

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.f}  {message}");
