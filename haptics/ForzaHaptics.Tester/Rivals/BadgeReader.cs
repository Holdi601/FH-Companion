using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Read the one-character class badge, which the OCR engine refuses to read alone.
/// </summary>
/// <remarks>
/// Measured against Windows.Media.Ocr on this machine: a lone "C" on white returns
/// NOTHING, "CC" returns nothing, "CCC" reads. Two-character tokens fail too, so
/// "S1" is just as invisible. The engine wants something word-shaped.
///
/// So the badge is not handed over as it sits. The glyph is cut out of its badge --
/// found as the pixels that are none of the region's few flat background colours,
/// which is what a badge on a card on a sky reduces to -- and then repeated into a
/// word the engine will read. "CCC" comes back, the repeat is undone, and the class
/// is C.
///
/// FIVE copies, not three, because the two-character classes need the length:
/// measured, "S1S1S1" returns nothing while "S1S1S1S1S1" reads. And the digit comes
/// back as a letter ("SISISISISI"), so the unrepeated token is put through a small
/// confusion table before it is believed.
///
/// The alternative was to read the card's "C 484" line, which is the FEATURED car's
/// class and PI: on a Spec Racing event that is the spec car and not the
/// restriction, so it answers a different question with total confidence.
/// </remarks>
internal static class BadgeReader
{
    /// <summary>Colours covering this much of the region count as background.</summary>
    private const double BackgroundShare = 0.85;

    /// <summary>Luminance distance at which a pixel stops being background.</summary>
    private const int Distinct = 40;

    /// <summary>How tall the tiled word is made before the OCR sees it.</summary>
    private const int TargetGlyphHeight = 150;

    /// <summary>
    /// The glyph in this region, repeated into a word, or null if there is no glyph.
    /// </summary>
    /// <summary>The glyph alone, cleaned to dark-on-white, for the shape matcher.</summary>
    public static Bitmap? Isolate(Bitmap region) => Wordify(region, 1);

    public static Bitmap? Wordify(Bitmap region, int copies = 5)
    {
        var found = FindGlyph(region);
        if (found is null)
        {
            return null;
        }
        var (box, ink, maskWidth) = found.Value;

        // Redrawn dark on white rather than cut out as pixels: the badge's own
        // colour carries no information here, and a plain glyph is what the engine
        // was trained on -- the orange-on-yellow crop read at 4K and not at 1440p.
        //
        // Drawn as GREY, not black-or-white. A hard threshold leaves jagged strokes,
        // and Windows OCR returned nothing at all for a cleanly legible "S1S1S1"
        // built that way; keeping each pixel's distance from the background as its
        // darkness keeps the original antialiasing, and it reads.
        byte strongest = 1;
        for (var y = 0; y < box.Height; y++)
        {
            for (var x = 0; x < box.Width; x++)
            {
                var at = (box.Top + y) * maskWidth + box.Left + x;
                if (at >= 0 && at < ink.Length && ink[at] > strongest)
                {
                    strongest = ink[at];
                }
            }
        }

        using var glyph = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppArgb);
        var glyphData = glyph.LockBits(new Rectangle(0, 0, box.Width, box.Height),
                                       ImageLockMode.WriteOnly,
                                       PixelFormat.Format32bppArgb);
        unsafe
        {
            var scan = (byte*)glyphData.Scan0;
            for (var y = 0; y < box.Height; y++)
            {
                for (var x = 0; x < box.Width; x++)
                {
                    var at = (box.Top + y) * maskWidth + box.Left + x;
                    var raw = at >= 0 && at < ink.Length ? ink[at] : (byte)0;
                    var darkness = (byte)Math.Clamp(raw * 255 / strongest, 0, 255);
                    var value = (byte)(255 - darkness);
                    var to = y * glyphData.Stride + x * 4;
                    scan[to] = value;
                    scan[to + 1] = value;
                    scan[to + 2] = value;
                    scan[to + 3] = 255;
                }
            }
        }
        glyph.UnlockBits(glyphData);

        var scale = Math.Max(1.0, (double)TargetGlyphHeight / glyph.Height);
        var width = Math.Max(1, (int)(glyph.Width * scale));
        var height = Math.Max(1, (int)(glyph.Height * scale));
        // Close enough together to read as one word, far enough apart not to touch.
        var gap = Math.Max(4, width / 8);
        var pad = Math.Max(12, height / 3);

        var word = new Bitmap(width * copies + gap * (copies - 1) + pad * 2,
                              height + pad * 2, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(word))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            for (var i = 0; i < copies; i++)
            {
                g.DrawImage(glyph, pad + i * (width + gap), pad, width, height);
            }
        }
        return word;
    }

    /// <summary>
    /// Undo the repetition: "ccc" is C, "s1s1s1" is S1.
    /// </summary>
    /// <remarks>
    /// The engine also lower-cases and occasionally drops one copy, so a unit that
    /// repeats at least twice is enough, and the result is upper-cased.
    /// </remarks>
    public static string? Unrepeat(string text, int copies = 5)
    {
        var compact = new string((text ?? string.Empty)
            .Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (compact.Length == 0)
        {
            return null;
        }
        for (var unit = 1; unit <= 2; unit++)
        {
            if (compact.Length < unit * 2)
            {
                continue;
            }
            var head = compact[..unit];
            var repeats = 0;
            for (var at = 0; at + unit <= compact.Length; at += unit)
            {
                if (compact.Substring(at, unit) != head)
                {
                    break;
                }
                repeats++;
            }
            if (repeats >= Math.Min(2, copies))
            {
                return Canonical(head);
            }
        }
        // One copy read and the rest lost: still the answer, if it is short enough.
        return compact.Length <= 2 ? Canonical(compact) : null;
    }

    /// <summary>
    /// Undo the letter-for-digit reads a recogniser makes on a two-glyph class.
    /// </summary>
    /// <remarks>
    /// Measured on this engine: "S1" comes back as "SI" or "Sl", "S2" survives. Only
    /// the character AFTER an S is corrected, so nothing else can be turned into a
    /// class it is not -- and the caller still checks the result against the classes
    /// the dataset actually has.
    /// </remarks>
    private static string Canonical(string token)
    {
        if (token.Length != 2 || token[0] != 'S')
        {
            return token;
        }
        var digit = token[1] switch
        {
            'I' or 'L' or '|' or '!' or 'T' => '1',
            'Z' => '2',
            _ => token[1],
        };
        return $"S{digit}";
    }

    /// <summary>How far two pixels may differ and still be the same plate.</summary>
    private const int PlateTolerance = 26;

    /// <summary>A plate is a strong colour, not a shade of the sky.</summary>
    private const int PlateChroma = 45;

    /// <summary>Colour distance at which a pixel on the plate becomes the glyph.</summary>
    private const int PlateInk = 70;

    /// <summary>
    /// Where the glyph sits, tried the reliable way first.
    /// </summary>
    /// <remarks>
    /// Two different backgrounds occur behind the badge. On a menu it is a few flat
    /// areas, and the flats method below finds the glyph by elimination. On the Event
    /// Sign Up card it is a PHOTOGRAPH -- a crowd, green banners, yellow barriers --
    /// which has no flats at all: every method that works by elimination lights up
    /// across the whole picture, the box comes out bigger than a glyph can be, and the
    /// read returns nothing. That is what a real press produced, measured 2026-09-09:
    /// all three routes read, class empty with no source at all.
    ///
    /// So the plate is found first. A class badge is a small SOLID square of one
    /// strongly coloured paint with a white character on it, and a photograph holds no
    /// such thing -- its coloured areas are neither uniform nor rectangular. Once the
    /// square is known, the glyph is simply what is not the paint, inside it.
    /// </remarks>
    private static (Rectangle Box, byte[] Ink, int Width)? FindGlyph(Bitmap region)
        => FindGlyphOnPlate(region) ?? FindGlyphOnFlats(region);

    /// <summary>The chroma of a packed colour: how far it is from a grey.</summary>
    private static int Chroma(int rgb)
    {
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
    }

    /// <summary>The largest channel difference between two packed colours.</summary>
    private static int Apart(int a, int b)
        => Math.Max(Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)),
             Math.Max(Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)),
                      Math.Abs((a & 0xFF) - (b & 0xFF))));

    /// <summary>
    /// The glyph on a solid coloured plate, wherever that plate happens to sit.
    /// </summary>
    /// <remarks>
    /// A candidate has to be a plate on every count: one paint within
    /// <see cref="PlateTolerance"/>, coloured rather than grey, filling its own
    /// bounding box the way a rectangle does, roughly square, and carrying a
    /// character. A green banner in the photograph is coloured and large but ragged,
    /// so it fails the fill; a stretch of yellow barrier is rectangular but slanted
    /// and blank, so it fails both the fill and the character.
    /// </remarks>
    private static (Rectangle Box, byte[] Ink, int Width)? FindGlyphOnPlate(Bitmap region)
    {
        var width = region.Width;
        var height = region.Height;
        var pixels = new int[width * height];
        var data = region.LockBits(new Rectangle(0, 0, width, height),
                                   ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var scan = (byte*)data.Scan0;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var at = y * data.Stride + x * 4;
                        pixels[y * width + x] =
                            (scan[at + 2] << 16) | (scan[at + 1] << 8) | scan[at];
                    }
                }
            }
        }
        finally
        {
            region.UnlockBits(data);
        }

        var total = pixels.Length;
        var floor = Math.Max(200, (int)(total * 0.008));
        var ceiling = (int)(total * 0.25);
        var seen = new bool[total];
        var queue = new Queue<int>();

        Rectangle? bestBox = null;
        var bestPaint = 0;
        var bestArea = 0;

        for (var start = 0; start < total; start++)
        {
            if (seen[start])
            {
                continue;
            }
            var paint = pixels[start];
            if (Chroma(paint) < PlateChroma)
            {
                seen[start] = true;
                continue;
            }

            void Consider(int next)
            {
                if (seen[next] || Apart(pixels[next], paint) > PlateTolerance)
                {
                    return;
                }
                seen[next] = true;
                queue.Enqueue(next);
            }

            queue.Clear();
            queue.Enqueue(start);
            seen[start] = true;
            var area = 0;
            var left = width;
            var right = -1;
            var top = height;
            var bottom = -1;
            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                area++;
                var x = at % width;
                var y = at / width;
                if (x < left) { left = x; }
                if (x > right) { right = x; }
                if (y < top) { top = y; }
                if (y > bottom) { bottom = y; }
                // Four neighbours; a plate is solid, so it needs no diagonals.
                if (x > 0) { Consider(at - 1); }
                if (x < width - 1) { Consider(at + 1); }
                if (y > 0) { Consider(at - width); }
                if (y < height - 1) { Consider(at + width); }
            }

            if (area < floor || area > ceiling || area <= bestArea)
            {
                continue;
            }
            var box = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            // Solid: the paint fills its own box, less the character it carries.
            if (area < box.Width * box.Height * 0.70)
            {
                continue;
            }
            var ratio = (double)box.Width / Math.Max(1, box.Height);
            if (ratio < 0.55 || ratio > 1.9)
            {
                continue;
            }
            // And something is written on it: pixels inside that are not the paint.
            var written = 0;
            for (var y = box.Top; y < box.Bottom; y++)
            {
                for (var x = box.Left; x < box.Right; x++)
                {
                    if (Apart(pixels[y * width + x], paint) > PlateInk)
                    {
                        written++;
                    }
                }
            }
            var share = (double)written / Math.Max(1, box.Width * box.Height);
            if (share < 0.03 || share > 0.45)
            {
                continue;
            }
            bestBox = box;
            bestPaint = paint;
            bestArea = area;
        }

        if (bestBox is not { } plate)
        {
            return null;
        }

        // The character is whatever is not the paint, and only INSIDE the plate -- so
        // the padding the caller adds can never drag the photograph back in.
        var ink = new byte[total];
        var glyphLeft = plate.Right;
        var glyphRight = plate.Left - 1;
        var glyphTop = plate.Bottom;
        var glyphBottom = plate.Top - 1;
        for (var y = plate.Top; y < plate.Bottom; y++)
        {
            for (var x = plate.Left; x < plate.Right; x++)
            {
                var at = y * width + x;
                var apart = Apart(pixels[at], bestPaint);
                if (apart <= PlateInk)
                {
                    continue;
                }
                ink[at] = (byte)Math.Clamp(apart - PlateInk, 1, 255);
                if (x < glyphLeft) { glyphLeft = x; }
                if (x > glyphRight) { glyphRight = x; }
                if (y < glyphTop) { glyphTop = y; }
                if (y > glyphBottom) { glyphBottom = y; }
            }
        }
        if (glyphRight < glyphLeft || glyphBottom < glyphTop)
        {
            return null;
        }
        var found = Rectangle.FromLTRB(glyphLeft, glyphTop,
                                       glyphRight + 1, glyphBottom + 1);
        var padX = Math.Max(2, found.Width / 8);
        var padY = Math.Max(2, found.Height / 8);
        found.Inflate(padX, padY);
        found.Intersect(new Rectangle(0, 0, width, height));
        return (found, ink, width);
    }

    /// <summary>
    /// Where the glyph sits: the pixels that are none of the flat background colours.
    /// </summary>
    /// <remarks>
    /// A class badge sits on a card which sits on the world, so the region holds a
    /// handful of large flat areas and one small dark shape. Taking the most common
    /// luminances until 85 % of the region is accounted for identifies the flats
    /// without knowing any of their colours -- which matters, because the card's
    /// colour changes with the event.
    /// </remarks>
    private static (Rectangle Box, byte[] Ink, int Width)? FindGlyphOnFlats(Bitmap region)
    {
        var data = region.LockBits(new Rectangle(0, 0, region.Width, region.Height),
                                   ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var histogram = new int[256];
            var luminance = new byte[region.Width * region.Height];
            unsafe
            {
                var scan = (byte*)data.Scan0;
                for (var y = 0; y < region.Height; y++)
                {
                    for (var x = 0; x < region.Width; x++)
                    {
                        var at = y * data.Stride + x * 4;
                        var value = (byte)((scan[at] * 29 + scan[at + 1] * 150
                                            + scan[at + 2] * 77) >> 8);
                        luminance[y * region.Width + x] = value;
                        histogram[value]++;
                    }
                }
            }

            // The flats, most common first, until most of the region is covered.
            var total = luminance.Length;
            var flats = new List<int>();
            var covered = 0;
            var order = Enumerable.Range(0, 256).OrderByDescending(v => histogram[v]);
            foreach (var value in order)
            {
                if (covered >= total * BackgroundShare || flats.Count >= 6)
                {
                    break;
                }
                flats.Add(value);
                // Count the whole neighbourhood: antialiasing spreads a flat colour
                // over a few adjacent buckets.
                for (var near = Math.Max(0, value - 6); near <= Math.Min(255, value + 6); near++)
                {
                    covered += histogram[near];
                    histogram[near] = 0;
                }
            }

            int left = region.Width, top = region.Height, right = -1, bottom = -1;
            var hits = 0;
            // How far past "background" each pixel is, 0..255, so the redraw can
            // keep the glyph's soft edges.
            var ink = new byte[luminance.Length];
            for (var y = 0; y < region.Height; y++)
            {
                for (var x = 0; x < region.Width; x++)
                {
                    var value = luminance[y * region.Width + x];
                    var nearest = flats.Count == 0 ? 255
                        : flats.Min(flat => Math.Abs(flat - value));
                    if (nearest <= Distinct)
                    {
                        continue;
                    }
                    // Raw distance past the threshold. It is normalised later
                    // against the strongest ink in the glyph: a fixed scale left the
                    // strokes grey whenever a background happened to sit near the
                    // glyph's own luminance, and grey strokes do not read.
                    ink[y * region.Width + x] = (byte)Math.Clamp(nearest - Distinct, 1, 255);
                    hits++;
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }

            if (right < 0 || hits < 12)
            {
                return null;
            }
            // A glyph is a small part of its region; anything larger is the layout
            // itself, which means the mask is pointing at the wrong place.
            var box = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            if (box.Width > region.Width * 0.75 || box.Height > region.Height * 0.85)
            {
                return null;
            }
            var padX = Math.Max(2, box.Width / 8);
            var padY = Math.Max(2, box.Height / 8);
            box.Inflate(padX, padY);
            box.Intersect(new Rectangle(0, 0, region.Width, region.Height));
            return (box, ink, region.Width);
        }
        finally
        {
            region.UnlockBits(data);
        }
    }
}
