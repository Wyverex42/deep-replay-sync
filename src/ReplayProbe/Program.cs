// ReplayProbe: read-only UI Automation dump of Deepchart / Deepdom windows.
//
// Usage:
//   dotnet run -- [process-name-hint ...] [--out <dir>] [--raw] [--watch <seconds>] [--max-nodes <n>]
//
//   process-name-hint  substrings of the process names to probe (default: deepchart deepdom)
//   --out              folder for the full tree dumps (default: ./probe-output)
//   --raw              walk the raw UIA view instead of the control view (more elements, more noise)
//   --watch <seconds>  after the dump, poll every text/value/toggle for that long and report what changed.
//                      Run it while a replay is playing to find the replay clock.
//   --max-nodes <n>    stop walking a window after n elements (default 20000)

using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

var hints = new List<string>();
string outDir = Path.Combine(Environment.CurrentDirectory, "probe-output");
bool raw = false;
double watchSeconds = 0;
int maxNodes = 20000;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outDir = args[++i]; break;
        case "--raw": raw = true; break;
        case "--watch": watchSeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--max-nodes": maxNodes = int.Parse(args[++i]); break;
        default: hints.Add(args[i]); break;
    }
}
if (hints.Count == 0) hints.AddRange(["deepchart", "deepdom"]);

Directory.CreateDirectory(outDir);

var processes = Process.GetProcesses()
    .Where(p => hints.Any(h => p.ProcessName.Contains(h, StringComparison.OrdinalIgnoreCase)))
    .ToDictionary(p => p.Id, p => p.ProcessName);

if (processes.Count == 0)
{
    Console.WriteLine($"No running process matches: {string.Join(", ", hints)}. Start the apps first.");
    return 1;
}

using var automation = new UIA3Automation();
var walker = raw ? automation.TreeWalkerFactory.GetRawViewWalker() : automation.TreeWalkerFactory.GetControlViewWalker();

// All top-level windows of the matched processes: main windows and floating/detached ones
var windows = automation.GetDesktop().FindAllChildren()
    .Select(w => (Window: w, Pid: Safe(() => w.Properties.ProcessId.ValueOrDefault, 0)))
    .Where(x => processes.ContainsKey(x.Pid))
    .ToList();

Console.WriteLine($"Processes: {string.Join(", ", processes.Select(p => $"{p.Value} ({p.Key})"))}");
Console.WriteLine($"Top-level windows: {windows.Count}   view: {(raw ? "raw" : "control")}");
Console.WriteLine();

var allNodes = new List<Node>();
int windowIndex = 0;
foreach (var (window, pid) in windows)
{
    windowIndex++;
    var nodes = new List<Node>();
    Walk(window, 0, "Window", nodes);
    allNodes.AddRange(nodes);

    string proc = processes[pid];
    string title = Clip(Safe(() => window.Properties.Name.ValueOrDefault, "") ?? "", 60);
    string framework = Safe(() => window.Properties.FrameworkId.ValueOrDefault, "") ?? "";
    string file = Path.Combine(outDir, $"{proc}-w{windowIndex}.txt");
    File.WriteAllLines(file, nodes.Select(n => n.ToTreeLine()));

    Console.WriteLine($"=== {proc} pid {pid}  window {windowIndex}: \"{title}\"  framework={framework}  elements={nodes.Count}{(nodes.Count >= maxNodes ? " (truncated)" : "")}");
    Console.WriteLine($"    full tree: {file}");

    var actionable = nodes.Where(n => n.IsActionable).ToList();
    int noId = actionable.Count(n => n.Id.Length == 0);
    Console.WriteLine($"    actionable controls: {actionable.Count}, without AutomationId: {noId}");

    var candidates = nodes.Where(n => n.IsCandidate).ToList();
    if (candidates.Count == 0)
    {
        Console.WriteLine("    no replay-looking controls or time texts found");
    }
    foreach (var n in candidates)
    {
        string warn = n.IsActionable && n.Id.Length == 0 ? "  [no AutomationId]" : "";
        Console.WriteLine($"    {n.Summary()}{warn}");
        Console.WriteLine($"        path: {n.Path}");
    }
    Console.WriteLine();
}

if (watchSeconds > 0) Watch(allNodes, watchSeconds);

return 0;

// ---------------------------------------------------------------------------------------------

void Walk(AutomationElement el, int depth, string path, List<Node> nodes)
{
    if (nodes.Count >= maxNodes || depth > 80) return;
    var node = Node.Read(el, depth, path);
    if (node is null) return;
    nodes.Add(node);

    var typeCounts = new Dictionary<string, int>();
    AutomationElement? child = Safe(() => walker.GetFirstChild(el), null);
    while (child is not null && nodes.Count < maxNodes)
    {
        string type = Safe(() => child.Properties.ControlType.ValueOrDefault.ToString(), "?");
        string id = Safe(() => child.Properties.AutomationId.ValueOrDefault, "") ?? "";
        string name = Safe(() => child.Properties.Name.ValueOrDefault, "") ?? "";
        typeCounts[type] = typeCounts.GetValueOrDefault(type) + 1;
        string segment = id.Length > 0 ? $"{type}[id={id}]"
            : name.Length > 0 ? $"{type}[name={Clip(name, 40)}]"
            : $"{type}[#{typeCounts[type]}]";
        Walk(child, depth + 1, $"{path}/{segment}", nodes);
        var current = child;
        child = Safe(() => walker.GetNextSibling(current), null);
    }
}

void Watch(List<Node> nodes, double seconds)
{
    var watched = nodes.Where(n => n.HasReadableState).ToList();
    Console.WriteLine($"=== Watching {watched.Count} texts/values/toggles for {seconds:0.#} s (start the replay now if it is not playing)...");

    var first = watched.ToDictionary(n => n, n => n.ReadState());
    var last = new Dictionary<Node, string?>(first);
    var changes = watched.ToDictionary(n => n, _ => 0);

    var sw = Stopwatch.StartNew();
    int polls = 0;
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        foreach (var n in watched)
        {
            var v = n.ReadState();
            if (v != last[n]) { changes[n]++; last[n] = v; }
        }
        polls++;
        Thread.Sleep(250);
    }

    var changed = watched.Where(n => changes[n] > 0).OrderByDescending(n => changes[n]).ToList();
    Console.WriteLine($"    {polls} polls, {changed.Count} elements changed");
    foreach (var n in changed)
    {
        Console.WriteLine($"    {changes[n],4}x  {n.Summary()}");
        Console.WriteLine($"           \"{Clip(first[n] ?? "", 50)}\" -> \"{Clip(last[n] ?? "", 50)}\"");
        Console.WriteLine($"           path: {n.Path}");
    }
}

static T Safe<T>(Func<T> f, T fallback)
{
    try { return f(); } catch { return fallback; }
}

static string Clip(string s, int max)
{
    s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    return s.Length <= max ? s : s[..(max - 1)] + "…";
}

sealed partial class Node
{
    public required AutomationElement El { get; init; }
    public int Depth { get; init; }
    public string Path { get; init; } = "";
    public string Type { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Help { get; init; } = "";
    public string Class { get; init; } = "";
    public string Patterns { get; init; } = "";
    public string? Value { get; init; }
    public string? Toggle { get; init; }
    public string? Range { get; init; }
    public bool Enabled { get; init; }
    public bool Offscreen { get; init; }
    public Rectangle Rect { get; init; }

    public bool IsActionable => Patterns.Contains("Invoke") || Patterns.Contains("Toggle")
                                || Patterns.Contains("RangeValue") || Patterns.Contains("SelectionItem")
                                || Patterns.Contains("ExpandCollapse");

    public bool HasReadableState => Value is not null || Toggle is not null || Range is not null
                                    || (Type == nameof(ControlType.Text) && Name.Length > 0);

    // Replay-looking: an actionable control with a replay keyword, or any text that looks like a time/date
    public bool IsCandidate
    {
        get
        {
            string ident = $"{Id} {Name} {Help} {Class}";
            if (IsActionable && Keywords().IsMatch(ident)) return true;
            string text = $"{Name} {Value}";
            return TimeLike().IsMatch(text) || DateLike().IsMatch(text);
        }
    }

    [GeneratedRegex(@"\b(play|pause|stop|replay|playback|speed|step|forward|rewind|back|fast|slow|resume|seek|goto|go to|jump|riprod|pausa|veloc|avanti|indietro|wiedergabe|geschw|vorw|zur(ü|u)ck)|\b\d+(\.\d+)?x\b|(?-i:(?<=[a-z])(Play|Pause|Stop|Replay|Speed|Step|Forward|Rewind|Back))", RegexOptions.IgnoreCase)]
    private static partial Regex Keywords();

    [GeneratedRegex(@"\b\d{1,2}:\d{2}(:\d{2})?([.,]\d+)?\b")]
    private static partial Regex TimeLike();

    [GeneratedRegex(@"\b\d{1,4}[/.\-]\d{1,2}[/.\-]\d{1,4}\b")]
    private static partial Regex DateLike();

    public static Node? Read(AutomationElement el, int depth, string path)
    {
        try
        {
            var p = el.Properties;
            var pat = new List<string>();
            string? value = null, toggle = null, range = null;

            if (Try(() => el.Patterns.Invoke.IsSupported)) pat.Add("Invoke");
            if (Try(() => el.Patterns.Toggle.IsSupported))
            {
                pat.Add("Toggle");
                toggle = TryGet(() => el.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault.ToString());
            }
            if (Try(() => el.Patterns.Value.IsSupported))
            {
                pat.Add("Value");
                value = TryGet(() => el.Patterns.Value.Pattern.Value.ValueOrDefault);
            }
            if (Try(() => el.Patterns.RangeValue.IsSupported))
            {
                pat.Add("RangeValue");
                range = TryGet(() =>
                {
                    var r = el.Patterns.RangeValue.Pattern;
                    return $"{r.Value.ValueOrDefault} [{r.Minimum.ValueOrDefault}..{r.Maximum.ValueOrDefault}]";
                });
            }
            if (Try(() => el.Patterns.SelectionItem.IsSupported)) pat.Add("SelectionItem");
            if (Try(() => el.Patterns.ExpandCollapse.IsSupported)) pat.Add("ExpandCollapse");
            if (Try(() => el.Patterns.Text.IsSupported)) pat.Add("Text");

            return new Node
            {
                El = el,
                Depth = depth,
                Path = path,
                Type = TryGet(() => p.ControlType.ValueOrDefault.ToString()) ?? "?",
                Id = TryGet(() => p.AutomationId.ValueOrDefault) ?? "",
                Name = TryGet(() => p.Name.ValueOrDefault) ?? "",
                Help = TryGet(() => p.HelpText.ValueOrDefault) ?? "",
                Class = TryGet(() => p.ClassName.ValueOrDefault) ?? "",
                Enabled = Try(() => p.IsEnabled.ValueOrDefault),
                Offscreen = Try(() => p.IsOffscreen.ValueOrDefault),
                Rect = TryGetStruct(() => p.BoundingRectangle.ValueOrDefault),
                Patterns = string.Join(",", pat),
                Value = value,
                Toggle = toggle,
                Range = range,
            };
        }
        catch
        {
            return null; // element vanished while reading
        }
    }

    public string? ReadState()
    {
        if (Value is not null) return TryGet(() => El.Patterns.Value.Pattern.Value.ValueOrDefault);
        if (Range is not null) return TryGet(() => El.Patterns.RangeValue.Pattern.Value.ValueOrDefault.ToString());
        if (Toggle is not null)
        {
            // A play/pause button often keeps its toggle state and changes its name or tooltip instead
            return TryGet(() => $"{El.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault} {El.Properties.Name.ValueOrDefault} {El.Properties.HelpText.ValueOrDefault}");
        }
        return TryGet(() => El.Properties.Name.ValueOrDefault);
    }

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append($"[{Type}]");
        if (Id.Length > 0) sb.Append($" id=\"{Id}\"");
        if (Name.Length > 0) sb.Append($" name=\"{Clip(Name, 50)}\"");
        if (Help.Length > 0) sb.Append($" help=\"{Clip(Help, 50)}\"");
        if (Value is not null) sb.Append($" value=\"{Clip(Value, 40)}\"");
        if (Toggle is not null) sb.Append($" toggle={Toggle}");
        if (Range is not null) sb.Append($" range={Range}");
        if (Patterns.Length > 0) sb.Append($" ({Patterns})");
        if (!Enabled) sb.Append(" disabled");
        if (Offscreen) sb.Append(" offscreen");
        return sb.ToString();
    }

    // Identity first, geometry last, so dumps of two runs can be diffed
    public string ToTreeLine() =>
        $"{new string(' ', Depth * 2)}{Summary()} class=\"{Class}\" rect={Rect.X},{Rect.Y},{Rect.Width}x{Rect.Height}";

    static bool Try(Func<bool> f) { try { return f(); } catch { return false; } }
    static string? TryGet(Func<string?> f) { try { return f(); } catch { return null; } }
    static Rectangle TryGetStruct(Func<Rectangle> f) { try { return f(); } catch { return default; } }
    static string Clip(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
