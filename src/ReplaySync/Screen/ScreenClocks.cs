using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

/// <summary>A value read from a window's pixels. FrameAt is the Stopwatch time in seconds of that image.</summary>
sealed record Seen<T>(T Value, double FrameAt);

/// <summary>
/// Reads the clocks which the two apps draw with seconds, on a thread of its own so a slow read can not delay a
/// pause. Only looks at pixels: the chart and DOM windows have order buttons and never get input, focus or messages.
/// </summary>
sealed class ScreenClocks : IDisposable
{
    const int PollMs = 200;

    readonly Watched<AxisReading> _dom;
    readonly Watched<int> _chart;
    readonly ManualResetEvent _stop = new(false);
    readonly Thread _thread;

    public ScreenClocks(string chartProcess, string domProcess, Action<string> log)
    {
        var axis = new AxisReader();
        var countdown = new CountdownReader();
        _dom = new(domProcess, "Deepdom's time axis", AxisReader.Needs, axis.Read, log);
        _chart = new(chartProcess, "Deepchart's bar countdown", CountdownReader.Needs, countdown.Read, log);
        _thread = new Thread(Run) { IsBackground = true, Name = "Screen clocks", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Null: not readable right now.</summary>
    public Seen<AxisReading>? Dom => _dom.Latest;
    public Seen<int>? Chart => _chart.Latest;

    void Run()
    {
        // Window rectangles in real pixels, as the captured images are
        Native.SetThreadDpiAwarenessContext(Native.PerMonitorAwareV2);
        do
        {
            _dom.Poll();
            _chart.Poll();
        }
        while (!_stop.WaitOne(PollMs));
        _dom.Dispose();
        _chart.Dispose();
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(3000);
    }

    /// <summary>One app: finds the window which shows its clock and keeps reading it.</summary>
    sealed class Watched<T>(string processName, string what, Func<Rectangle, float, Rectangle> needs,
        Func<Frame, Rectangle, float, T?> read, Action<string> log)
        : IDisposable where T : struct
    {
        const double SearchEvery = 3, GiveUpAfter = 4;

        WindowCapture? _capture;
        IntPtr _window, _chartArea;
        double _nextSearch, _lastGood;
        int _candidate;
        bool? _wasReadable;
        volatile Seen<T>? _latest;

        public Seen<T>? Latest => _latest;

        public void Poll()
        {
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            try
            {
                if (_capture is null || _capture.IsClosed)
                {
                    Forget();
                    if (now < _nextSearch) return;
                    _nextSearch = now + SearchEvery;
                    Open(now);
                }
                else if (!Native.IsWindowVisible(_window) || Native.IsIconic(_window) || !Native.IsWindowVisible(_chartArea))
                {
                    // Minimized, closed or on another tab: the last image is no longer the truth
                    Forget();
                }
                else if (ReadNewest() is { } image)
                {
                    var (frame, value) = image;
                    _latest = value is { } v ? new Seen<T>(v, frame.At) : null;
                    if (value is not null) _lastGood = now;
                }

                if (_capture is not null && _latest is null && now - _lastGood > GiveUpAfter)
                {
                    // Perhaps it is in another window of the app
                    _candidate++;
                    Forget();
                }
            }
            catch (Exception e)
            {
                if (_wasReadable != false) log($"Can not capture {what}: {e.Message}");
                _wasReadable = false;
                Forget();
            }
            Report(now);
        }

        (Frame frame, T? value)? ReadNewest()
        {
            float scale = Native.GetDpiForWindow(_window) / 96f;
            var chart = ChartAreaInImage();
            if (_capture!.TakeNewest(needs(chart, scale)) is not { } frame) return null;
            chart.Offset(-frame.Origin.X, -frame.Origin.Y);
            return (frame, read(frame, chart, scale));
        }

        void Report(double now)
        {
            if (_latest is not null && _wasReadable != true)
            {
                _wasReadable = true;
                log($"Reading {what}: time to the second");
            }
            else if (_latest is null && _wasReadable != false && now - _lastGood > GiveUpAfter && _lastGood > 0)
            {
                _wasReadable = false;
                log($"Can not read {what} (minimized, hidden or zoomed?): using the minute clock");
            }
        }

        void Open(double now)
        {
            var found = ChartWindows();
            if (found.Count == 0)
            {
                if (_lastGood == 0) _lastGood = now; // so that not finding it gets reported
                return;
            }
            (_window, _chartArea) = found[_candidate % found.Count];
            _capture = new WindowCapture(_window);
            _lastGood = now;
        }

        // The charts are native child windows: the largest visible one of each top-level window
        List<(IntPtr window, IntPtr area)> ChartWindows()
        {
            var pids = new HashSet<int>();
            foreach (var process in Process.GetProcessesByName(processName))
                using (process)
                    pids.Add(process.Id);

            var found = new List<(IntPtr window, IntPtr area, long size)>();
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out int pid);
                if (!pids.Contains(pid) || !Native.IsWindowVisible(window) || Native.IsIconic(window)) return true;

                IntPtr best = IntPtr.Zero;
                long size = 0;
                Native.EnumChildWindows(window, (child, _) =>
                {
                    if (Native.IsWindowVisible(child) && Native.GetWindowRect(child, out var r) && r.Area > size)
                    {
                        size = r.Area;
                        best = child;
                    }
                    return true;
                }, IntPtr.Zero);
                if (best != IntPtr.Zero) found.Add((window, best, size));
                return true;
            }, IntPtr.Zero);

            return found.OrderByDescending(f => f.size).Select(f => (f.window, f.area)).ToList();
        }

        Rectangle ChartAreaInImage()
        {
            // The image covers the window's visible frame, without the invisible resize borders
            if (Native.DwmGetWindowAttribute(_window, Native.ExtendedFrameBounds, out var outer, Marshal.SizeOf<Native.Rect>()) != 0)
                Native.GetWindowRect(_window, out outer);
            Native.GetWindowRect(_chartArea, out var inner);
            return Rectangle.FromLTRB(inner.Left - outer.Left, inner.Top - outer.Top, inner.Right - outer.Left, inner.Bottom - outer.Top);
        }

        void Forget()
        {
            _capture?.Dispose();
            _capture = null;
            _latest = null;
        }

        public void Dispose() => Forget();
    }

    static class Native
    {
        public static readonly IntPtr PerMonitorAwareV2 = -4;
        public const int ExtendedFrameBounds = 9;

        public struct Rect
        {
            public int Left, Top, Right, Bottom;
            public readonly long Area => (long)(Right - Left) * (Bottom - Top);
        }

        public delegate bool EnumProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    }
}
