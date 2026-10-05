using System.Drawing;
using System.Drawing.Imaging;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Identify a class badge by its SHAPE, against the eight tokens it can possibly be.
/// </summary>
/// <remarks>
/// The OCR route is unreliable here, and provably so. Measured against
/// Windows.Media.Ocr on this machine: a lone "C" returns nothing, "CC" returns
/// nothing, "CCC" reads; "S1S1S1" returns nothing while "S1S1S1S1S1" reads; and the
/// same five-fold "S1" built from the game's OWN glyph returns nothing even though
/// it is clean, black and legible, because its strokes are thinner than a rendered
/// font's. Tuning pixels against a black box is not a fix.
///
/// But the answer set here has EIGHT members. So the glyph is compared against
/// A B C D R X S1 S2 rendered locally: both sides are reduced to their ink, scaled
/// into one grid and overlapped. That is deterministic, needs no engine, and cannot
/// return a class that does not exist.
///
/// The OCR still gets first refusal in <see cref="RivalsScreenReader"/>, because
/// when it does read a badge it has read the real glyph rather than the nearest
/// lookalike. This decides the cases where it comes back empty.
/// </remarks>
internal static class BadgeShapes
{
    private const int Grid = 48;

    /// <summary>Below this the glyph is not one of the eight, and nothing is claimed.</summary>
    private const double Accept = 0.52;

    private static readonly string[] Candidates =
        { "A", "B", "C", "D", "R", "X", "S1", "S2" };

    private static readonly Dictionary<string, (bool[] Ink, double Aspect)> Templates
        = new(StringComparer.Ordinal);

    /// <summary>The best-matching class token, or null if nothing fits well enough.</summary>
    public static (string Token, double Score)? Identify(Bitmap glyph,
                                                         IEnumerable<string>? allowed = null)
    {
        var (ink, aspect) = Reduce(glyph);
        if (ink is null)
        {
            return null;
        }
        var wanted = allowed is null
            ? Candidates
            : Candidates.Where(c => allowed.Contains(c, StringComparer.Ordinal)).ToArray();

        string? best = null;
        var bestScore = 0.0;
        foreach (var token in wanted)
        {
            var template = TemplateFor(token);
            var score = Overlap(ink, template.Ink);
            // An "S1" is twice as wide as a "C"; without this the two compete.
            var ratio = Math.Min(aspect, template.Aspect) / Math.Max(aspect, template.Aspect);
            score *= 0.5 + 0.5 * ratio;
            if (score > bestScore)
            {
                best = token;
                bestScore = score;
            }
        }
        return best is not null && bestScore >= Accept ? (best, bestScore) : null;
    }

    private static (bool[] Ink, double Aspect) TemplateFor(string token)
    {
        if (Templates.TryGetValue(token, out var cached))
        {
            return cached;
        }
        using var font = new Font("Segoe UI Semibold", 96, GraphicsUnit.Pixel);
        using var canvas = new Bitmap(420, 220, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawString(token, font, Brushes.Black, 30, 30);
        }
        var made = Reduce(canvas);
        var value = (made.Ink ?? new bool[Grid * Grid], made.Aspect);
        Templates[token] = value;
        return value;
    }

    /// <summary>Tight-crop the ink and sample it into one fixed grid.</summary>
    private static (bool[]? Ink, double Aspect) Reduce(Bitmap source)
    {
        var data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
                                   ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int left = source.Width, top = source.Height, right = -1, bottom = -1;
        var dark = new bool[source.Width * source.Height];
        try
        {
            unsafe
            {
                var scan = (byte*)data.Scan0;
                for (var y = 0; y < source.Height; y++)
                {
                    for (var x = 0; x < source.Width; x++)
                    {
                        var at = y * data.Stride + x * 4;
                        var lum = (scan[at] * 29 + scan[at + 1] * 150 + scan[at + 2] * 77) >> 8;
                        if (lum >= 128)
                        {
                            continue;
                        }
                        dark[y * source.Width + x] = true;
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }
            }
        }
        finally
        {
            source.UnlockBits(data);
        }
        if (right < 0 || bottom < 0)
        {
            return (null, 1);
        }

        var width = right - left + 1;
        var height = bottom - top + 1;
        var grid = new bool[Grid * Grid];
        for (var gy = 0; gy < Grid; gy++)
        {
            for (var gx = 0; gx < Grid; gx++)
            {
                // One sample per cell: the glyph is far larger than the grid, so
                // this costs nothing and loses nothing that matters at 48x48.
                var sx = left + (int)((gx + 0.5) * width / Grid);
                var sy = top + (int)((gy + 0.5) * height / Grid);
                grid[gy * Grid + gx] = dark[sy * source.Width + sx];
            }
        }
        return (grid, (double)width / height);
    }

    private static double Overlap(bool[] a, bool[] b)
    {
        var both = 0;
        var either = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            if (a[i] && b[i])
            {
                both++;
            }
            if (a[i] || b[i])
            {
                either++;
            }
        }
        return either == 0 ? 0 : (double)both / either;
    }
}
