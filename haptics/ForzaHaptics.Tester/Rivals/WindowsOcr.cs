using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>One line of text the OCR found, in screen coordinates.</summary>
internal readonly record struct OcrLine(string Text, double X, double Y)
{
    /// <summary>Breite und Hoehe der Zeile (0, wo sie nicht bekannt ist -- aeltere Aufrufer).</summary>
    /// <remarks>Seit 2026-09-29: um im Autogitter "JAHR MARKE" der Zeile darueber zuzuordnen, braucht es die Mitte.</remarks>
    public double W { get; init; }
    public double H { get; init; }
}

/// <summary>
/// Text off a bitmap, using the OCR engine that ships with Windows.
/// </summary>
/// <remarks>
/// Windows.Media.Ocr and not the scanner's RapidOCR: this runs inside the game's
/// own process tree during play, so it must not pull in ONNX Runtime, a model
/// directory or a Python interpreter, and it must not touch the GPU -- the game
/// needs it. The repository already used this engine for leaderboard frames
/// (`scripts/windows_ocr.ps1`), so it is a known quantity on this machine.
///
/// The engine is created once and reused; creating it per frame costs more than
/// the recognition does.
/// </remarks>
internal sealed class WindowsOcr
{
    private readonly OcrEngine? _engine;

    public WindowsOcr()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? OcrEngine.AvailableRecognizerLanguages
                      .Select(OcrEngine.TryCreateFromLanguage)
                      .FirstOrDefault(e => e is not null);
    }

    public bool Available => _engine is not null;

    public string LanguageTag => _engine?.RecognizerLanguage?.LanguageTag ?? "none";

    /// <summary>Every line the engine found, with its top-left corner.</summary>
    public List<OcrLine> Read(Bitmap bitmap)
    {
        var lines = new List<OcrLine>();
        if (_engine is null)
        {
            return lines;
        }

        using var software = ToSoftwareBitmap(bitmap);
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        var result = _engine.RecognizeAsync(software).AsTask().GetAwaiter().GetResult();
        Leistung.Ocr(uhr.ElapsedTicks);
        foreach (var line in result.Lines)
        {
            var text = line.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            double x = 0, y = 0, w = 0, h = 0;
            var first = line.Words.FirstOrDefault();
            if (first is not null)
            {
                x = first.BoundingRect.X;
                y = line.Words.Min(w => w.BoundingRect.Y);
                w = line.Words.Max(v => v.BoundingRect.X + v.BoundingRect.Width) - x;
                h = line.Words.Max(v => v.BoundingRect.Y + v.BoundingRect.Height) - y;
            }
            lines.Add(new OcrLine(text, x, y) { W = w, H = h });
        }
        return lines;
    }

    /// <summary>
    /// GDI+ bitmap into a WinRT SoftwareBitmap, one locked row-copy each way.
    /// </summary>
    /// <remarks>
    /// Written through the bitmap's own buffer rather than through
    /// <c>CreateCopyFromBuffer</c>: the <c>AsBuffer</c> extension that would need
    /// does not exist under CsWinRT, and locking the destination avoids a second
    /// full-frame copy anyway.
    /// </remarks>
    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        var software = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bitmap.Width,
                                          bitmap.Height, BitmapAlphaMode.Premultiplied);
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var buffer = software.LockBuffer(BitmapBufferAccessMode.Write);
            using var reference = buffer.CreateReference();
            unsafe
            {
                // A plain cast throws under CsWinRT ("Invalid cast from
                // WinRT.IInspectable"): the projected object is not a classic COM
                // RCW, so the QueryInterface has to go through WinRT's own cast.
                reference.As<IMemoryBufferByteAccess>()
                         .GetBuffer(out var target, out var capacity);
                var plane = buffer.GetPlaneDescription(0);
                var widthBytes = bitmap.Width * 4;
                for (var row = 0; row < bitmap.Height; row++)
                {
                    var destination = target + plane.StartIndex + plane.Stride * row;
                    if (plane.StartIndex + plane.Stride * row + widthBytes > capacity)
                    {
                        break;
                    }
                    Buffer.MemoryCopy((void*)(data.Scan0 + data.Stride * row), destination,
                                      widthBytes, widthBytes);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return software;
    }
}

/// <summary>Raw access to a WinRT memory buffer, so a frame is copied once.</summary>
[ComImport]
[Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
