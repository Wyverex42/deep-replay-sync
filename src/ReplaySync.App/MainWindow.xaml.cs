using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeepReplaySync;

/// <summary>Shows the runner's latest status and posts commands to it. It never calls UI Automation itself.</summary>
public partial class MainWindow : Window
{
    const int MaxLogChars = 40_000;
    const string None = "—";
    static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(6);

    readonly Settings _settings = Settings.Load();
    readonly SyncRunner _runner;
    readonly DispatcherTimer _refresh;

    string? _confirmedDate;
    long? _syncRequestedAt;
    bool _closing, _closed;

    public MainWindow()
    {
        InitializeComponent();
        FillSettings(_settings);
        TopmostBox.IsChecked = Topmost = _settings.AlwaysOnTop;

        _runner = new SyncRunner(_settings.ToOptions());
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => UpdateDisplay(), Dispatcher);
        _refresh.Start();
    }

    internal void ShowUiError(string message) => LastMessage.Text = $"Display error: {message}";

    // Display

    void UpdateDisplay()
    {
        DrainLog();
        if (_closing) return;

        if (_runner.Fatal is { } fatal)
        {
            ShowBanner(new(Level.Danger, "The sync stopped", $"Pause Deepchart by hand and restart this app. {fatal}"));
            GapText.Text = None;
            SyncButton.IsEnabled = FollowButton.IsEnabled = StartButton.IsEnabled = false;
            return;
        }
        if (_runner.Latest is not { } status) return;

        var s = status.Sync;
        bool working = s.Activity is not null;
        bool stale = !working && Stopwatch.GetElapsedTime(status.PublishedAt) > StaleAfter;

        ShowBanner(stale
            ? new(Level.Danger, "Not updating", "A window is not answering. Pause Deepchart by hand if in doubt.")
            : Banner.For(s));
        ShowGap(s, live: !stale && !working);
        ShowApp(s.Dom, DomTime, DomState);
        ShowApp(s.Chart, ChartTime, ChartState);
        ShowStats(s);

        // A command is pending until a newer status arrives: a second click must not undo the first
        if (_syncRequestedAt is { } requested && status.PublishedAt > requested) _syncRequestedAt = null;
        SyncButton.IsEnabled = !working && !_runner.CleanStarting && _syncRequestedAt is null;
        SyncButton.Content = s.SyncOn ? "Stop sync (pauses chart)" : "Start sync";
        SyncButton.Background = Brush(s.SyncOn ? "Raised" : "Accent");
        FollowButton.IsEnabled = !working && s is { SyncOn: true, ChartHeld: false, Chart.Connected: true }
                                 && s.Chart.Reading is { Playing: false };

        UpdateStartPanel(status);
    }

    void ShowBanner(Banner banner)
    {
        BannerTitle.Text = banner.Title;
        BannerDetail.Text = banner.Detail;
        BannerBox.Background = Brush(banner.Level switch
        {
            Level.Good => "GoodBack",
            Level.Holding => "HoldingBack",
            Level.Danger => "DangerBack",
            _ => "IdleBack",
        });
    }

    void ShowGap(SyncSnapshot s, bool live)
    {
        // An old number would look like a current one
        if (!live || !s.Chart.Connected || !s.Dom.Connected || !s.Chart.Responding || !s.Dom.Responding)
        {
            GapText.Text = None;
            GapText.Foreground = Brush("Muted");
            BoundText.Text = "No current reading";
            return;
        }

        if (s is { Ahead: not null, AheadBound: > 0 })
        {
            // The best guess may say behind, but that is not certain
            GapText.Text = "May be ahead";
            GapText.Foreground = Brush("WarningText");
        }
        else if (s.Ahead is { } ahead)
        {
            GapText.Text = ahead > 0 ? $"{ahead:0.0} s ahead" : $"{-ahead:0.0} s behind";
            GapText.Foreground = Brush(ahead > 0 ? "DangerText" : "GoodText");
        }
        else
        {
            GapText.Text = "Not measured yet";
            GapText.Foreground = Brush("Muted");
        }

        BoundText.Text = s.AheadBound switch
        {
            { } b when b > 0 => $"Worst case: could be up to {b:0.0} s ahead",
            { } b => $"Worst case: still {-b:0.0} s behind",
            null => "Worst case: unknown",
        };
    }

    void ShowApp(AppSnapshot app, System.Windows.Controls.TextBlock time, System.Windows.Controls.TextBlock state)
    {
        time.Text = app.DisplayedMinute is not { } minute ? None
            : app.Anchored ? $"~{app.Estimate:HH:mm:ss}"
            : $"{minute:HH:mm}:??";
        time.Foreground = Brush(app.Connected && app.Responding ? "Text" : "Muted");

        state.Text = !app.Connected ? "Replay Manage window not found"
            : !app.Responding ? "Not responding"
            : app.Reading is not { } r ? "Connected"
            : r.Minute is null ? $"Can not read the date \"{r.Text}\""
            : $"{(r.Playing ? "Playing" : "Paused")}, speed x{r.Speed}, rate {app.Rate:0.00} {(app.RateMeasured ? "measured" : "assumed")}";
    }

    void ShowStats(SyncSnapshot s)
    {
        string worst = s.WorstAhead is not { } w ? ""
            : s.Violations > 0 ? $"  |  ahead at {s.Violations} of Deepdom's minute changes (worst {w:0.0} s)"
            : $"  |  never ahead at a minute change (closest {-w:0.0} s)";
        StatsText.Text = $"{s.Pauses} pauses{worst}";
        StatsText.Foreground = Brush(s.Violations > 0 ? "DangerText" : "Muted");

        bool differ = s.Chart.Reading is { } c && s.Dom.Reading is { } d && c.Speed != d.Speed;
        SpeedWarning.Visibility = differ ? Visibility.Visible : Visibility.Collapsed;
        if (differ) SpeedWarning.Text = $"Speeds differ: Deepchart x{s.Chart.Reading?.Speed}, Deepdom x{s.Dom.Reading?.Speed}";
    }

    void UpdateStartPanel(SyncRunner.Status status)
    {
        var s = status.Sync;
        bool running = _runner.CleanStarting;

        StartRunning.Visibility = Show(running);
        StartConfirm.Visibility = Show(!running && _confirmedDate is not null);
        StartEdit.Visibility = Show(!running && _confirmedDate is null);

        if (running)
        {
            RunningText.Text = s.Activity is { } activity
                ? $"{activity} ({Stopwatch.GetElapsedTime(status.ActivitySince).TotalSeconds:0} s)"
                : "Starting";
        }
        bool both = s.Chart.Connected && s.Dom.Connected;
        StartButton.IsEnabled = both && s.Activity is null;
        ConfirmYes.IsEnabled = both && s.Activity is null;
        UseDomTime.IsEnabled = s.Dom.Reading is not null;
    }

    void DrainLog()
    {
        bool added = false;
        while (_runner.TryTakeLog(out var line))
        {
            LogBox.AppendText(line + Environment.NewLine);
            LastMessage.Text = line;
            added = true;
        }
        if (!added) return;
        if (LogBox.Text.Length > MaxLogChars) LogBox.Text = LogBox.Text[^(MaxLogChars / 2)..];
        LogBox.ScrollToEnd();
    }

    Brush Brush(string key) => (Brush)FindResource(key);
    static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // Space is a habit in replay software: a clicked button must not keep the focus and fire again on Space
    void DropFocus()
    {
        FocusManager.SetFocusedElement(this, this);
        Keyboard.Focus(this);
    }

    // Sync

    void OnSyncClick(object sender, RoutedEventArgs e)
    {
        DropFocus();
        if (_runner.Latest is not { } status) return;
        SyncButton.IsEnabled = false;
        _syncRequestedAt = Stopwatch.GetTimestamp();
        if (status.Sync.SyncOn) _runner.DisableSync();
        else _runner.EnableSync();
    }

    void OnFollowClick(object sender, RoutedEventArgs e)
    {
        DropFocus();
        FollowButton.IsEnabled = false;
        _runner.Follow();
    }

    // Not Click: toggling through UI Automation (screen readers) does not raise it
    void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (((ToggleButton)sender).IsChecked == true)
        {
            foreach (var tab in new[] { StartTab, SettingsTab, LogTab })
                if (tab != sender) tab.IsChecked = false;
        }

        StartPanel.Visibility = Show(StartTab.IsChecked == true);
        SettingsPanel.Visibility = Show(SettingsTab.IsChecked == true);
        LogPanel.Visibility = Show(LogTab.IsChecked == true);
        // Scrolling has no effect while collapsed, so once it is laid out
        if (LogTab.IsChecked == true) Dispatcher.BeginInvoke(LogBox.ScrollToEnd, DispatcherPriority.Loaded);

        _confirmedDate = null;
        if (StartTab.IsChecked == true && StartDate.Text.Length == 0) FillDomTime();
        DropFocus();
    }

    // Clean start

    // A full date only: a bare "10:15" would parse as today
    [GeneratedRegex(@"^[0-9]{2}/[0-9]{2}/[0-9]{4} [0-9]{2}:[0-9]{2}\z")]
    private static partial Regex FullDate();

    void FillDomTime()
    {
        if (_runner.Latest?.Sync.Dom.Reading is { } reading) StartDate.Text = reading.Text.Trim();
    }

    void OnUseDomTimeClick(object sender, RoutedEventArgs e)
    {
        FillDomTime();
        DropFocus();
    }

    void OnStartDateChanged(object sender, RoutedEventArgs e) => StartError.Text = "";

    void OnStartClick(object sender, RoutedEventArgs e)
    {
        DropFocus();
        string text = StartDate.Text.Trim();
        if (!FullDate().IsMatch(text) || ReplayApp.ParseMinute(text) is null)
        {
            StartError.Text = "Enter a full, valid date and time, like 06/01/2026 10:15.";
            return;
        }
        _confirmedDate = text;
        ConfirmText.Text = $"Both replays are paused and moved to {text}, then played. Loading can take a few minutes, "
                           + "and Deepdom sometimes crashes while doing it.";
        StartEdit.Visibility = Visibility.Collapsed;
        StartConfirm.Visibility = Visibility.Visible;
    }

    void OnConfirmNoClick(object sender, RoutedEventArgs e)
    {
        DropFocus();
        _confirmedDate = null;
        StartConfirm.Visibility = Visibility.Collapsed;
        StartEdit.Visibility = Visibility.Visible;
    }

    void OnConfirmYesClick(object sender, RoutedEventArgs e)
    {
        DropFocus();
        if (_confirmedDate is not { } date) return;
        _confirmedDate = null;
        SyncButton.IsEnabled = false;
        StartConfirm.Visibility = Visibility.Collapsed;
        StartRunning.Visibility = Visibility.Visible;
        _runner.CleanStart(date);
    }

    void OnCancelStartClick(object sender, RoutedEventArgs e)
    {
        DropFocus();
        _runner.CancelCleanStart();
    }

    // Settings

    void FillSettings(Settings s)
    {
        MarginBox.Text = s.Margin.ToString(CultureInfo.InvariantCulture);
        HysteresisBox.Text = s.Hysteresis.ToString(CultureInfo.InvariantCulture);
        SafetyBox.Text = s.DomSafety.ToString(CultureInfo.InvariantCulture);
        CatchUpBox.Text = s.CatchUp.ToString(CultureInfo.InvariantCulture);
    }

    void OnDefaultSettingsClick(object sender, RoutedEventArgs e)
    {
        FillSettings(new Settings());
        SettingsMessage.Text = "Defaults filled in. Press Apply to use them.";
        DropFocus();
    }

    void OnApplySettingsClick(object sender, RoutedEventArgs e)
    {
        DropFocus();
        static double Parse(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

        double margin = Parse(MarginBox.Text), hysteresis = Parse(HysteresisBox.Text);
        double safety = Parse(SafetyBox.Text), catchUp = Parse(CatchUpBox.Text);

        string? error = !Settings.ValidMargin(margin) ? "Pause closer than: 0 to 60 seconds."
            : !Settings.ValidHysteresis(hysteresis) ? "Resume after a further: 0 to 60 seconds."
            : !Settings.ValidSafety(safety) ? "Deepdom safety: 0.1 to 1."
            : !Settings.ValidCatchUp(catchUp) ? "Forward when behind by: 30 to 86400 seconds."
            : null;
        if (error is not null)
        {
            SettingsMessage.Text = $"Not applied. {error}";
            return;
        }

        (_settings.Margin, _settings.Hysteresis, _settings.DomSafety, _settings.CatchUp) = (margin, hysteresis, safety, catchUp);
        _runner.ApplyTuning(_settings);
        SettingsMessage.Text = _settings.Save() is { } failed ? $"Applied, but not saved: {failed}" : "Applied and saved.";
    }

    void OnTopmostClick(object sender, RoutedEventArgs e)
    {
        Topmost = _settings.AlwaysOnTop = TopmostBox.IsChecked == true;
        if (_settings.Save() is { } failed) SettingsMessage.Text = $"Not saved: {failed}";
        DropFocus();
    }

    // Window

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        const int UseImmersiveDarkMode = 20;
        int on = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, UseImmersiveDarkMode, ref on, sizeof(int));
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closed) return;
        // The sync thread first pauses Deepchart. Waiting for it here would block the UI thread, so close afterwards.
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        ShowBanner(new(Level.Holding, "Closing", "Leaving Deepchart paused if it was syncing."));
        IsEnabled = false;

        var stopped = _runner.StopAsync();
        bool safe = await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(30))) == stopped && await stopped;
        if (!safe)
            MessageBox.Show(this, "Deepchart could not be paused. Pause it by hand: nothing holds it back now.", Title);

        _refresh.Stop();
        _closed = true;
        // Not from inside OnClosing
        _ = Dispatcher.BeginInvoke(Close);
    }
}
