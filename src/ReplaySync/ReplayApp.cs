using System.Diagnostics;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

/// <summary>
/// The "Replay Manage" window of one app, driven through UI Automation. Only the replay controls are touched:
/// the start date box (which shows the live replay time while playing), Play, Pause and the speed box.
/// </summary>
sealed class ReplayApp(string label, string processName, UIA3Automation automation)
{
    public string Label { get; } = label;

    AutomationElement? _clock, _play, _pause, _speed, _set, _reset, _forward;
    Process? _process;

    public bool IsConnected => _clock is not null;
    /// <summary>Only Deepchart has a Forward button (jump ahead without a reset).</summary>
    public bool CanForward => _forward is not null;

    /// <summary>False when the process this window belonged to has exited (crashed or closed).</summary>
    public bool IsAlive
    {
        get
        {
            try { return _process is not null && !_process.HasExited; }
            catch { return false; }
        }
    }

    public bool TryConnect()
    {
        Disconnect();
        var processes = Process.GetProcessesByName(processName);
        var pids = processes.Select(p => p.Id).ToHashSet();
        if (pids.Count == 0) return false;

        var cf = automation.ConditionFactory;
        foreach (var window in automation.GetDesktop().FindAllChildren())
        {
            try
            {
                int pid = window.Properties.ProcessId.ValueOrDefault;
                if (!pids.Contains(pid)) continue;
                if (window.Properties.Name.ValueOrDefault != "Replay Manage") continue;

                _clock = window.FindFirstChild(cf.ByAutomationId("dtp_IniDt"))?.FindFirstChild(cf.ByAutomationId("PART_Txt"));
                _speed = window.FindFirstChild(cf.ByAutomationId("nudSpeed"))?.FindFirstChild(cf.ByAutomationId("PART_Txt"));
                _play = window.FindFirstChild(cf.ByAutomationId("btnPlay"));
                _pause = window.FindFirstChild(cf.ByAutomationId("btnPause"));
                _set = window.FindFirstChild(cf.ByAutomationId("btnSet"));
                _reset = window.FindFirstChild(cf.ByAutomationId("btnReset"));
                _forward = window.FindFirstChild(cf.ByAutomationId("btnForward"));
                _process = processes.First(p => p.Id == pid);
                if (_clock is not null && _play is not null && _pause is not null) return true;
            }
            catch
            {
                // window closed while searching
            }
        }
        Disconnect();
        return false;
    }

    void Disconnect() => _clock = _play = _pause = _speed = _set = _reset = _forward = null;

    /// <summary>
    /// Deepchart only: types a later date into the date box and presses Forward. The replay jumps ahead with no
    /// feedback in the replay window. Returns what failed, or null.
    /// </summary>
    public string? ForwardTo(string text)
    {
        if (_forward is null) return "this app has no Forward button";
        if (TypeDate(text) is { } error) return error;
        return Invoke(_forward) ? null : "the Forward button is disabled";
    }

    /// <summary>
    /// Reset, type the date, Set: the only way to go back in time, and Deepdom's only way to move at all. The app
    /// then loads the data: wait for CanPlay. Returns what failed, or null.
    /// </summary>
    public string? ResetAndSet(string text)
    {
        if (!IsEnabled(_set))
        {
            if (!Invoke(_reset)) return "the Reset button is disabled";
            if (!WaitFor(() => IsEnabled(_set), 10)) return "the Set button did not enable after Reset";
        }
        if (TypeDate(text) is { } error) return error;
        return Invoke(_set) ? null : "the Set button is disabled";
    }

    /// <summary>True when Play is enabled: the replay is loaded and paused.</summary>
    public bool CanPlay => IsEnabled(_play);

    string? TypeDate(string text)
    {
        try
        {
            _clock!.Patterns.Value.Pattern.SetValue(text);
            return null;
        }
        catch (Exception e)
        {
            return $"could not type the date: {e.Message}";
        }
    }

    static bool IsEnabled(AutomationElement? e)
    {
        try { return e is not null && e.Properties.IsEnabled.Value; }
        catch { return false; }
    }

    static bool WaitFor(Func<bool> condition, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>Reads the replay state. False when the window is gone or the app does not answer.</summary>
    public bool TryRead(out ReplayReading reading) => Read(out reading) == ReadStatus.Ok;

    /// <summary>
    /// Reads the replay state. Unresponsive: the app did not answer within the automation timeout (busy or frozen
    /// UI thread): the connection is kept, try again. Gone: the window or the process is gone: reconnect.
    /// </summary>
    public ReadStatus Read(out ReplayReading reading)
    {
        reading = default;
        if (_clock is null || _play is null || _pause is null) return ReadStatus.Gone;
        try
        {
            string text = _clock.Patterns.Value.Pattern.Value.Value;
            bool playing = _pause.Properties.IsEnabled.Value;
            int speed = 1;
            if (_speed is not null && int.TryParse(_speed.Patterns.Value.Pattern.Value.Value, out int s) && s > 0) speed = s;
            reading = new ReplayReading(ParseMinute(text), text, playing, speed);
            return ReadStatus.Ok;
        }
        catch (FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            Disconnect();
            return ReadStatus.Gone;
        }
        catch
        {
            if (IsAlive) return ReadStatus.Unresponsive;
            Disconnect();
            return ReadStatus.Gone;
        }
    }

    public bool Play() => Invoke(_play);
    public bool Pause() => Invoke(_pause);

    /// <summary>
    /// Presses Play (or Pause) until the app really is playing (or paused), for up to the given time. A busy app
    /// can time out the first presses.
    /// </summary>
    public bool EnsurePlaying(bool playing, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (TryRead(out var r) && r.Playing == playing) return true;
            if (playing) Play(); else Pause();
            Thread.Sleep(300);
        }
        return TryRead(out var last) && last.Playing == playing;
    }

    static bool Invoke(AutomationElement? button)
    {
        try
        {
            if (button is null || !button.Properties.IsEnabled.Value) return false;
            button.Patterns.Invoke.Pattern.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    static readonly string[] Formats = ["dd/MM/yyyy HH:mm", "d/M/yyyy H:mm", "MM/dd/yyyy HH:mm", "yyyy-MM-dd HH:mm"];

    // Both apps format the date the same way, so the parse only has to be consistent, not "right"
    public static DateTime? ParseMinute(string text)
    {
        text = text.Trim();
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d)) return Truncate(d);
        if (DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return Truncate(d);
        return null;
    }

    static DateTime Truncate(DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0);
}

readonly record struct ReplayReading(DateTime? Minute, string Text, bool Playing, int Speed);

enum ReadStatus { Ok, Unresponsive, Gone }
