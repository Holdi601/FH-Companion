using System.Text.Json;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// Write one class table to JSON, so the C# scoring can be diffed against the site's.
/// </summary>
/// <remarks>
/// This exists for one reason: <c>scripts/test_rivals_advisor.py</c> runs the shipped
/// page's own JavaScript over the same payload and compares. Without it, this port
/// would be a third implementation of a changing rule with nothing watching it.
///
/// The output shape is deliberately identical to <c>scripts/dump_site_class_table.js</c>.
/// A WinExe has no console to write to, so the result goes to a file the caller names.
///
///   "FH Companion.exe" --dump-class-table out.json --class A --tracks "a,b"
/// </remarks>
internal static class RivalsDump
{
    public const string Flag = "--dump-class-table";

    /// <summary>Read one saved frame the way the overlay reads the screen.</summary>
    /// <remarks>
    /// The C# reader uses a different OCR engine from the Python one (Windows.Media
    /// .Ocr against RapidOCR), so "the matcher was ported correctly" is not the same
    /// claim as "it still reads the screen". `scripts/test_screen_reader.py` renders
    /// the Event Sign Up layout at three resolutions and runs BOTH readers over the
    /// same PNGs through this flag.
    /// </remarks>
    public const string ReadFlag = "--read-image";

    public static int Run(string[] args)
    {
        var outPath = Option(args, Flag) ?? "class-table.json";
        var klass = Option(args, "--class") ?? "A";
        var tracksArg = Option(args, "--tracks") ?? string.Empty;
        var clean = Option(args, "--clean") ?? "valid";
        var gear = Option(args, "--gear") ?? "any";
        var datasetPath = Option(args, "--dataset") ?? RivalsDataset.FindDefaultPath();

        if (datasetPath is null || !File.Exists(datasetPath))
        {
            File.WriteAllText(outPath,
                JsonSerializer.Serialize(new { error = $"no dataset at {datasetPath}" }));
            return 2;
        }

        var advisor = new RivalsAdvisor(RivalsDataset.Load(datasetPath));
        var tracks = tracksArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var (need, forbid) = advisor.Masks(clean, gear);
        var table = advisor.BuildClassTable(klass, tracks.Count > 0 ? tracks : null,
                                            null, need, forbid);

        var payload = new
        {
            klass,
            tracks = table.Tracks.Select(t => t.Track).ToList(),
            timeTracks = table.TimeTracks,
            shallowTracks = table.ShallowTracks,
            cars = table.Cars.Select(c => new
            {
                car = c.Car,
                name = advisor.CarName(c.Car),
                points = c.Points,
                ms = c.Ms,
                present = c.Present,
                thin = c.Thin,
            }).ToList(),
            byPoints = table.Cars.OrderByDescending(c => c.Points).Select(c => c.Car).ToList(),
            byTime = table.Cars.OrderBy(c => c.Ms).Select(c => c.Car).ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = false }));
        return 0;
    }

    /// <summary>Record why a dump failed where the caller will look for the answer.</summary>
    public static void WriteFailure(string[] args, Exception exception,
                                    string pathFlag = Flag)
    {
        try
        {
            var outPath = Option(args, pathFlag) ?? "class-table.json";
            File.WriteAllText(outPath, JsonSerializer.Serialize(new
            {
                error = exception.GetType().Name + ": " + exception.Message,
            }));
        }
        catch (Exception)
        {
            // Nothing left to do: the process is about to exit either way.
        }
    }

    /// <summary>Read a PNG and write what the screen reader made of it.</summary>
    public static int ReadImage(string[] args)
    {
        var imagePath = Option(args, ReadFlag) ?? string.Empty;
        var outPath = Option(args, "--out") ?? "read.json";
        var full = args.Contains("--full", StringComparer.OrdinalIgnoreCase);
        var datasetPath = Option(args, "--dataset") ?? RivalsDataset.FindDefaultPath();
        var configPath = Option(args, "--config");

        if (datasetPath is null || !File.Exists(datasetPath) || !File.Exists(imagePath))
        {
            File.WriteAllText(outPath, JsonSerializer.Serialize(new
            {
                error = $"missing dataset ({datasetPath}) or image ({imagePath})",
            }));
            return 2;
        }

        var advisor = new RivalsAdvisor(RivalsDataset.Load(datasetPath));
        var settings = OverlaySettings.Load(configPath);
        var reader = new RivalsScreenReader(advisor, settings);
        using var bitmap = new System.Drawing.Bitmap(imagePath);
        var state = reader.ReadBitmap(bitmap, full);

        File.WriteAllText(outPath, JsonSerializer.Serialize(new
        {
            ocr = reader.OcrLanguage,
            ocrAvailable = reader.OcrAvailable,
            tracks = state.Tracks,
            klass = state.Klass,
            klassSource = state.KlassSource,
            // The race type the panel would put in its title, worked out from the
            // routes rather than read off the screen.
            category = advisor.CategoryOf(state.Tracks),
            spec = state.Spec,
            isOffer = state.IsOffer,
            lines = state.Lines.Select(l => l.Text).ToList(),
            readMs = state.ReadMilliseconds,
        }));
        return 0;
    }

    /// <summary>Say which dataset this binary would load, and where it came from.</summary>
    /// <remarks>
    /// For the packager: <c>scripts/build_haptics_package.py</c> runs the published
    /// binary with this flag and refuses to zip anything that does not report the
    /// dataset it just put in the folder. A package whose data the app cannot find is
    /// precisely the failure that shows up only on someone else's machine, and only
    /// the binary itself can settle where it looks.
    ///
    /// Useful by hand too, when a panel shows numbers nobody expects: this names the
    /// file, the version, and the day it was built, without starting the overlay.
    /// </remarks>
    public const string SourceFlag = "--dataset-source";

    public static int DatasetSource(string[] args)
    {
        var outPath = Option(args, SourceFlag) ?? "dataset-source.json";
        var local = DatasetSync.LocalBest();

        // Loading it is the point. "The file is there" and "the app can use it" are
        // different claims, and a truncated copy passes the first one.
        var boards = 0;
        string? failure = null;
        if (local.Path is not null)
        {
            try
            {
                boards = RivalsDataset.Load(local.Path).Boards.Count;
            }
            catch (Exception exception)
            {
                failure = exception.GetType().Name + ": " + exception.Message;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(new
        {
            source = local.Source.ToString(),
            path = local.Path,
            version = local.Version,
            builtAt = local.BuiltAt,
            detail = local.Detail,
            declaredBoards = local.Boards,
            boards,
            error = failure,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return local.Path is not null && failure is null && boards > 0 ? 0 : 2;
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.FindIndex(args, a =>
            string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
