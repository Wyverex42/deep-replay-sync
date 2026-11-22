using System.Drawing;

/// <summary>
/// The right-most time label of Deepdom's axis. StepSeconds and Spacing (to the label left of it) identify the zoom.
/// BehindDataEnd: the label is under data which was already played, so its time can not be in the future.
/// </summary>
readonly record struct AxisReading(int SecondOfDay, int Right, int StepSeconds, int Spacing, bool BehindDataEnd);

/// <summary>Finds and reads Deepdom's time axis: the bottom row of the chart's native child window.</summary>
sealed class AxisReader
{
    // In device independent pixels, measured from the bottom / right of the chart
    const float RowTop = 14.4f, RowBottom = 1.6f, PriceAxisWidth = 72, DataBand = 13;
    const float LabelMinWidth = 34, LabelMaxWidth = 56, LabelGap = 5;
    const float InkThreshold = 0.4f;
    const int DataSaturation = 60;

    // One each: the subpixel position and so the best size can differ per label
    readonly DigitReader _last = new(darkText: false, digits: 6);
    readonly DigitReader _previous = new(darkText: false, digits: 6);

    /// <summary>The part of the window which has to be captured.</summary>
    public static Rectangle Needs(Rectangle chart, float scale) =>
        Rectangle.FromLTRB(chart.Left, chart.Bottom - (int)((RowTop + DataBand + 3) * scale), chart.Right, chart.Bottom);

    public AxisReading? Read(Frame frame, Rectangle chart, float scale)
    {
        chart.Intersect(new Rectangle(0, 0, frame.Width, frame.Height));
        int top = chart.Bottom - (int)MathF.Round(RowTop * scale), bottom = chart.Bottom - (int)MathF.Round(RowBottom * scale);
        if (top - DataBand * scale <= chart.Top || chart.Width < 100) return null;

        (int second, int left, int right)? last = null;
        int x = chart.Right - 2;
        // A few runs at most: the row can hold stray marks at the right
        for (int tries = 0; tries < 5; tries++)
        {
            if (NextRun(frame, chart.Left, x, top, bottom, (int)(LabelGap * scale)) is not { } run) break;
            x = run.Left - 1;
            if (run.Width < LabelMinWidth * scale || run.Width > LabelMaxWidth * scale) continue;

            var reader = last is null ? _last : _previous;
            if (reader.Read(frame.Bgra, frame.Stride, run) is not { } read || SecondOfDay(read.Digits) is not { } second)
            {
                // An unreadable right-most label must not make the one left of it look like the newest
                return last is { } l ? Result(frame, chart, scale, top, l, 0, 0) : null;
            }
            if (last is not { } newest)
            {
                last = (second, run.Left, run.Right);
                continue;
            }
            int step = (newest.second - second + 86400) % 86400;
            return Result(frame, chart, scale, top, newest, step, newest.right - run.Right);
        }
        return last is { } only ? Result(frame, chart, scale, top, only, 0, 0) : null;
    }

    static AxisReading Result(Frame frame, Rectangle chart, float scale, int rowTop,
        (int second, int left, int right) label, int step, int spacing)
    {
        // Played data is coloured (bars, outlines); the grid, zero line and empty space right of it are grey
        int center = (label.left + label.right) / 2;
        int from = chart.Right - (int)(PriceAxisWidth * scale);
        bool behind = false;
        for (int x = from; x >= center && !behind; x--)
            for (int y = rowTop - (int)(DataBand * scale); y < rowTop && !behind; y++)
                behind = frame.Saturation(x, y) > DataSaturation;
        return new AxisReading(label.second, label.right, step, spacing, behind);
    }

    static Rectangle? NextRun(Frame frame, int left, int from, int top, int bottom, int gap)
    {
        bool Lit(int x)
        {
            for (int y = top; y < bottom; y++)
                if (frame.Luminance(x, y) > InkThreshold) return true;
            return false;
        }

        int right = from;
        while (right >= left && !Lit(right)) right--;
        if (right < left) return null;

        int start = right;
        for (int x = right, empty = 0; x >= left && empty <= gap; x--)
        {
            if (Lit(x)) { start = x; empty = 0; }
            else empty++;
        }

        int first = bottom, last = top;
        for (int y = top; y < bottom; y++)
        {
            for (int x = start; x <= right; x++)
            {
                if (frame.Luminance(x, y) <= InkThreshold) continue;
                first = Math.Min(first, y);
                last = Math.Max(last, y);
            }
        }
        return Rectangle.FromLTRB(start, first, right + 1, last + 1);
    }

    static int? SecondOfDay(int[] d)
    {
        int h = d[0] * 10 + d[1], m = d[2] * 10 + d[3], s = d[4] * 10 + d[5];
        return h < 24 && m < 60 && s < 60 ? h * 3600 + m * 60 + s : null;
    }
}

/// <summary>Finds and reads Deepchart's bar countdown: dark text in a light grey box at the chart's bottom right.</summary>
sealed class CountdownReader
{
    const float SearchWidth = 110, SearchHeight = 70, BoxMinWidth = 24, BoxMinHeight = 12;
    const float BoxLight = 0.72f, InkThreshold = 0.5f;
    const int GreyTolerance = 24;

    readonly DigitReader _reader = new(darkText: true, digits: 4);

    /// <summary>Seconds left in the bar, or null.</summary>
    public static Rectangle Needs(Rectangle chart, float scale) =>
        Rectangle.FromLTRB(chart.Right - (int)(SearchWidth * scale), chart.Bottom - (int)(SearchHeight * scale), chart.Right, chart.Bottom);

    public int? Read(Frame frame, Rectangle chart, float scale)
    {
        chart.Intersect(new Rectangle(0, 0, frame.Width, frame.Height));
        var area = Rectangle.FromLTRB(
            Math.Max(chart.Left, chart.Right - (int)(SearchWidth * scale)),
            Math.Max(chart.Top, chart.Bottom - (int)(SearchHeight * scale)),
            chart.Right, chart.Bottom);
        if (FindBox(frame, area, scale) is not { } box) return null;

        int left = box.Right, right = -1, top = box.Bottom, bottom = -1;
        for (int y = box.Top + 2; y < box.Bottom - 2; y++)
        {
            for (int x = box.Left + 2; x < box.Right - 2; x++)
            {
                if (frame.Luminance(x, y) >= InkThreshold) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        if (right < 0) return null;

        if (_reader.Read(frame.Bgra, frame.Stride, Rectangle.FromLTRB(left, top, right + 1, bottom + 1)) is not { } read) return null;
        var d = read.Digits;
        int seconds = d[2] * 10 + d[3];
        return seconds < 60 ? (d[0] * 10 + d[1]) * 60 + seconds : null;
    }

    static Rectangle? FindBox(Frame frame, Rectangle area, float scale)
    {
        bool Light(int x, int y) => frame.Luminance(x, y) > BoxLight && frame.Saturation(x, y) < GreyTolerance;

        // The box's top edge is a long light run, then follow its left side down
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                if (!Light(x, y)) continue;
                int end = x;
                while (end < area.Right && Light(end, y)) end++;
                if (end - x >= BoxMinWidth * scale)
                {
                    int column = x + 1, bottom = y;
                    while (bottom < area.Bottom && Light(column, bottom)) bottom++;
                    if (bottom - y >= BoxMinHeight * scale) return Rectangle.FromLTRB(x, y, end, bottom);
                }
                x = end;
            }
        }
        return null;
    }
}
