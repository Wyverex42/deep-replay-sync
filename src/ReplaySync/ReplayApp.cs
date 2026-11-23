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

    AutomationElement? _clock, _play, _pause, _speed, _set, _reset, _forward, _window;
    Process? _process;

    public bool IsConnected => _clock is not null;
    public int? ProcessId => _process?.Id;
    /// <summary>Only Deepchart has a Forward button (jump ahead without a reset).</summary>
    public bool CanForward => _forward is not null;
    /// <summary>The window was found once, so there is a replay which may be playing.</summary>
    public bool WindowSeen { get; private set; }

    public bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName(processName);
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }
    }

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
        AutomationElement[] windows;
        // Times out as a whole while one of the apps hangs
        try { windows = automation.GetDesktop().FindAllChildren(); }
        catch { return false; }

        foreach (var window in windows)
        {
            try
            {
                int pid = window.Properties.ProcessId.ValueOrDefault;
                if (!pids.Contains(pid)) continue;
                if (window.Properties.Name.ValueOrDefault != "Replay Manage") continue;
                WindowSeen = true;

                _clock = window.FindFirstChild(cf.ByAutomationId("dtp_IniDt"))?.FindFirstChild(cf.ByAutomationId("PART_Txt"));
                _speed = window.FindFirstChild(cf.ByAutomationId("nudSpeed"))?.FindFirstChild(cf.ByAutomationId("PART_Txt"));
                _play = window.FindFirstChild(cf.ByAutomationId("btnPlay"));
                _pause = window.FindFirstChild(cf.ByAutomationId("btnPause"));
                _set = window.FindFirstChild(cf.ByAutomationId("btnSet"));
                _reset = window.FindFirstChild(cf.ByAutomationId("btnReset"));
                _forward = window.FindFirstChild(cf.ByAutomationId("btnForward"));
                _window = window;
                _process = processes.First(p => p.Id == pid);
                if (_clock is not null && _play is not null && _pause is not null && _set is not null && _reset is not null)
                    return true;
            }
            catch
            {
                // window closed while searching
            }
        }
        Disconnect();
        return false;
    }

    void Disconnect() => _clock = _play = _pause = _speed = _set = _reset = _forward = _window = null;

    /// <summary>The speed presets both apps show: buttons named "x1", "x8", ... with no AutomationId.</summary>
    public static readonly int[] Presets = [1, 8, 40, 100, 200, 400];

    /// <summary>Presses the preset button for this speed. False when there is none, or it was not pressed.</summary>
    public bool PressSpeedPreset(int speed)
    {
        try
        {
            if (_window is null) return false;
            var cf = automation.ConditionFactory;
            var button = _window.FindFirstDescendant(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button).And(cf.ByName($"x{speed}")));
            return Invoke(button);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Types a speed into the speed box. Whether the app applies it without Enter is what the caller checks.</summary>
    public bool TypeSpeed(int speed)
    {
        try
        {
            if (_speed is null) return false;
            _speed.Patterns.Value.Pattern.SetValue(speed.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deepchart only: types a later date into the date box and presses Forward. The replay jumps ahead with no
    /// feedback in the replay window. Returns what failed, or null.
    /// </summary>
    public string? ForwardTo(string text)
    {
        if (_forward is null) return "this app has no Forward button";
        if (!TypeDate(text)) return "could not type the date";
        return Forward() ? null : "could not press Forward";
    }

    // A press or a typed date which returns false may still be carried out: a busy app answers too late, but acts
    // once it continues. So look at the state before trying again.
    public bool Forward() => Invoke(_forward);
    public bool Reset() => Invoke(_reset);
    public bool Set() => Invoke(_set);

    public bool TypeDate(string text)
    {
        try
        {
            _clock!.Patterns.Value.Pattern.SetValue(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when Play is enabled: the replay is loaded and paused.</summary>
    public bool CanPlay => IsEnabled(_play);

    /// <summary>True when Set is enabled: the replay was reset.</summary>
    public bool CanSet => IsEnabled(_set);

    /// <summary>True when Set is known to be disabled. Not the opposite of CanSet: both are false without an answer.</summary>
    public bool SetIsDisabled
    {
        get
        {
            try { return _set is not null && !_set.Properties.IsEnabled.Value; }
            catch { return false; }
        }
    }

    static bool IsEnabled(AutomationElement? e)
    {
        try { return e is not null && e.Properties.IsEnabled.Value; }
        catch { return false; }
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
