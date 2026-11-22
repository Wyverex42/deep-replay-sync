using System.Drawing;
using System.Drawing.Drawing2D;

/// <summary>
/// Reads a time such as 10:27:36 or 00:24 drawn in Segoe UI, by comparing each digit with rendered ones.
/// Refuses rather than guesses. Not thread safe. On a recording of both apps it read 261 of 261 frames correctly,
/// where the Windows OCR engine read 60 % of the Deepdom labels.
/// </summary>
sealed class DigitReader(bool darkText, int digits)
{
    const float DigitAdvance = 0.539f, ColonAdvance = 0.217f;
    const float MinScore = 0.62f, MinLead = 0.07f;

    readonly Dictionary<(float em, int height, float dx, float dy), float[][]> _templates = new();
    float? _em;
    byte[] _lastPatch = [];
    Rectangle _lastInk;
    Result? _lastResult;

    public readonly record struct Result(int[] Digits, float Score, float Lead);

    /// <summary>ink: the box around the text's lit pixels.</summary>
    public Result? Read(byte[] bgra, int stride, Rectangle ink)
    {
        if (ink.Height is < 7 or > 30) return null;
        var patch = Patch(bgra, stride, ink);
        if (ink == _lastInk && patch.AsSpan().SequenceEqual(_lastPatch)) return _lastResult;
        _lastInk = ink;
        _lastPatch = patch;
        return _lastResult = Match(bgra, stride, ink);
    }

    // The pixels which matching looks at: the ink and a little around it
    static byte[] Patch(byte[] bgra, int stride, Rectangle ink)
    {
        var area = Rectangle.Inflate(ink, 6, 3);
        area.Intersect(new Rectangle(0, 0, stride / 4, bgra.Length / stride));
        var patch = new byte[area.Width * area.Height * 4];
        for (int y = 0; y < area.Height; y++)
            Buffer.BlockCopy(bgra, (area.Top + y) * stride + area.Left * 4, patch, y * area.Width * 4, area.Width * 4);
        return patch;
    }

    Result? Match(byte[] bgra, int stride, Rectangle ink)
    {
        float estimate = MathF.Round(ink.Height / 0.71f * 2) / 2;
        if (_em is { } known && Math.Abs(known - estimate) <= 1.5f && Best(bgra, stride, ink, known) is { } quick && Good(quick))
            return quick;

        Result? best = null;
        for (float em = estimate - 1.5f; em <= estimate + 1; em += 0.5f)
        {
            if (Best(bgra, stride, ink, em) is { } r && (best is null || r.Score > best.Value.Score))
            {
                best = r;
                _em = em;
            }
        }
        return best is { } b && Good(b) ? b : null;
    }

    static bool Good(Result r) => r.Score >= MinScore && r.Lead >= MinLead;

    Result? Best(byte[] bgra, int stride, Rectangle ink, float em)
    {
        float width = digits * DigitAdvance * em + (digits / 2 - 1) * ColonAdvance * em;
        if (Math.Abs(width - ink.Width) > 0.4f * em) return null;

        int cellW = (int)MathF.Ceiling(DigitAdvance * em) + 1, cellH = ink.Height + 2;
        int rows = bgra.Length / stride;
        Result? best = null;
        float bestTotal = float.MinValue;
        Span<float> patch = stackalloc float[cellW * cellH];

        for (float origin = ink.Left - 0.16f * em; origin <= ink.Left + 0.06f * em; origin += 0.25f)
        {
            foreach (float dy in (ReadOnlySpan<float>)[0f, -0.5f, 0.5f])
            {
                var found = new int[digits];
                float total = 0, worst = 1, lead = 1, x = origin;
                for (int slot = 0; slot < digits; slot++)
                {
                    int left = (int)MathF.Floor(x), top = ink.Top - 1;
                    float fraction = MathF.Round((x - left) * 4) / 4;
                    if (left < 0 || top < 0 || (left + cellW) * 4 > stride || top + cellH > rows) return best;

                    float sum = 0;
                    for (int py = 0; py < cellH; py++)
                    {
                        for (int px = 0; px < cellW; px++)
                        {
                            int o = (top + py) * stride + (left + px) * 4;
                            float v = (0.114f * bgra[o] + 0.587f * bgra[o + 1] + 0.299f * bgra[o + 2]) / 255f;
                            if (darkText) v = 1 - v;
                            patch[py * cellW + px] = v;
                            sum += v;
                        }
                    }
                    float mean = sum / patch.Length, norm = 0;
                    for (int i = 0; i < patch.Length; i++)
                    {
                        patch[i] -= mean;
                        norm += patch[i] * patch[i];
                    }
                    norm = MathF.Sqrt(norm);

                    float first = -1, second = -1;
                    var glyphs = Templates(em, fraction, dy, cellW, cellH);
                    for (int d = 0; d < 10; d++)
                    {
                        float dot = 0;
                        var glyph = glyphs[d];
                        for (int i = 0; i < patch.Length; i++) dot += patch[i] * glyph[i];
                        float score = norm > 0 ? dot / norm : 0;
                        if (score > first)
                        {
                            second = first;
                            first = score;
                            found[slot] = d;
                        }
                        else if (score > second)
                        {
                            second = score;
                        }
                    }
                    total += first;
                    worst = Math.Min(worst, first);
                    lead = Math.Min(lead, first - second);
                    x += DigitAdvance * em + (slot % 2 == 1 ? ColonAdvance * em : 0);
                }
                if (total > bestTotal)
                {
                    bestTotal = total;
                    best = new Result(found, worst, lead);
                }
            }
        }
        return best;
    }

    float[][] Templates(float em, float dx, float dy, int w, int h)
    {
        if (_templates.TryGetValue((em, h, dx, dy), out var cached)) return cached;

        using var family = new FontFamily("Segoe UI");
        using var format = new StringFormat(StringFormat.GenericTypographic);
        float top;
        using (var eight = new GraphicsPath())
        {
            eight.AddString("8", family, 0, em, new PointF(0, 0), format);
            top = eight.GetBounds().Top;
        }

        var all = new float[10][];
        for (int d = 0; d < 10; d++)
        {
            using var bitmap = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bitmap))
            using (var path = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                path.AddString(d.ToString(), family, 0, em, new PointF(dx, 1 - top + dy), format);
                g.Clear(Color.Black);
                g.FillPath(Brushes.White, path);
            }
            var t = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    t[y * w + x] = bitmap.GetPixel(x, y).R / 255f;

            float mean = t.Average();
            float norm = MathF.Sqrt(t.Sum(v => (v - mean) * (v - mean)));
            for (int i = 0; i < t.Length; i++) t[i] = norm > 0 ? (t[i] - mean) / norm : 0;
            all[d] = t;
        }
        return _templates[(em, h, dx, dy)] = all;
    }
}
