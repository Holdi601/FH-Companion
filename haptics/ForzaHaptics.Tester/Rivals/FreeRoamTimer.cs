using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>Eine Start-Ziel-Linie im freien Fahren: ein Ort und ein Name.</summary>
internal sealed class FreeRoamAnchor
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("z")] public float Z { get; set; }

    /// <summary>Woher die Linie stammt -- fuer die Anzeige, nicht fuer die Rechnung.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "manual";
}

/// <summary>
/// Time Attack in der freien Welt: die Uhr, die das Spiel dort nicht laufen laesst.
/// </summary>
/// <remarks>
/// ## Warum es diese Klasse gibt
///
/// In Horizon ist `IsRaceOn` auch im freien Fahren 1, aber `CurrentLap` bleibt auf 0
/// stehen -- GEMESSEN an 18.676 Paketen. Fuer die Aufzeichnung heisst das: die
/// Rundennummer wechselt nie, die Uhr laeuft nie, und darum wurde im freien Fahren
/// noch nie eine einzige Fahrt abgelegt. Der Streifen zeigte dort "free roam -- no
/// timed run", und das war ehrlich, aber es war auch alles.
///
/// Wer eine Strecke ueben will, ohne sie jedesmal ueber das Menue als Rivalen-Rennen
/// zu starten, braucht eine eigene Uhr. Die Telemetrie liefert alles dafuer: einen
/// Ort (`PositionX/Z`) und eine Zeit (`TimestampMS`). Was fehlt, ist die
/// Start-Ziel-Linie -- und die steht schon im Bestand: jede je gefahrene Strecke hat
/// eine, in ihrer <c>course.json</c>.
///
/// ## Wie eine Ueberfahrt erkannt wird: die groesste Annaeherung
///
/// Die naheliegende Loesung -- "Abstand kleiner als X, also drueber" -- ist ungenau
/// und zwar systematisch: bei 250 km/h liegen zwischen zwei Paketen rund 1,2 m, aber
/// der Rand eines 40-m-Kreises wird je nach Fahrlinie frueher oder spaeter erreicht.
/// Die Zeit haengt dann davon ab, wie weit seitlich man vorbeifaehrt.
///
/// Gemessen wird darum der Punkt der GROESSTEN ANNAEHERUNG. Das ist genau der Fuss
/// des Lotes auf die Linie durch den Ankerpunkt -- also das, was eine Start-Ziel-Linie
/// geometrisch ist -- und er ist unabhaengig davon, wie breit man vorbeifaehrt. Der
/// Zeitpunkt wird zwischen den beiden umgebenden Paketen interpoliert.
///
/// ## Warum zwei Radien
///
/// <see cref="ZoneRadius"/> (120 m) ist dieselbe Schwelle, mit der das Delta und der
/// Rundenbestand "dieselbe Strecke" bestimmen -- in ihr wird BEOBACHTET.
/// <see cref="AcceptRadius"/> (40 m) entscheidet, ob die Annaeherung als Ueberfahrt
/// ZAEHLT. Eine Parallelstrasse in 90 m Abstand liegt in der Zone, ist aber nicht die
/// Linie; mit nur einem Radius muesste man sich zwischen einem zu groben Bestand und
/// falschen Ueberfahrten entscheiden.
/// </remarks>
internal sealed class FreeRoamTimer
{
    /// <summary>In diesem Umkreis wird eine Annaeherung verfolgt.</summary>
    public const float ZoneRadius = RecordedLap.StartTolerance;

    /// <summary>So nah muss die groesste Annaeherung sein, damit sie zaehlt.</summary>
    /// <remarks>
    /// ENGER MACHT ES NICHT BESSER -- gemessen, nicht vermutet.
    ///
    /// Beim Nachmessen von 140 aufgezeichneten Runden (`--free-roam-replay`) kamen
    /// 139 auf ein Zehntelprozent genau heraus. Die eine Ausnahme war eine Fahrt
    /// ueber 6.818 m, die als 3.211 m gemessen wurde: die Strasse fuehrt zweimal an
    /// ihrem eigenen Startpunkt vorbei, bei 3.190 m auf 26,0 m und bei 3.999 m auf
    /// 20,3 m. Das sind gemessene Minima -- man faehrt dort wirklich ueber die Linie.
    ///
    /// 25 m statt 40 m wuerde die erste Vorbeifahrt verwerfen und die zweite
    /// trotzdem annehmen. Zu trennen waere das nur ueber die SOLLLAENGE der Strecke,
    /// und Laengenannahmen sind in diesem Bestand schon dreimal in einer Nacht
    /// schiefgegangen (siehe LapLibrary.Add).
    ///
    /// Die Regel bleibt also, und sie steht so auch im Handbuch: wer eine Strecke
    /// faehrt, die an ihrem Start wieder vorbeikommt, stoppt dort die Uhr. Der Hebel
    /// dagegen ist eine eigene Linie an einer Stelle, die nicht zweimal kommt.
    /// </remarks>
    public const float AcceptRadius = 40f;

    /// <summary>
    /// Der Abstand, um den man sich wieder entfernt haben muss, bevor eine
    /// Annaeherung als abgeschlossen gilt.
    /// </summary>
    /// <remarks>
    /// Ohne ihn wuerde jedes Rauschen im Abstand eine Ueberfahrt ausloesen, solange
    /// man in der Naehe steht. 25 m sind mehr als jedes Zittern und weniger als jede
    /// Weiterfahrt.
    /// </remarks>
    public const float LeaveHysteresis = 25f;

    /// <summary>Kuerzer als das ist keine Runde, sondern ein Wenden vor der Linie.</summary>
    public const float MinLapMetres = 400f;

    /// <summary>
    /// Laenger als das ist keine Runde mehr, sondern eine Fahrt durch die Landschaft.
    /// </summary>
    /// <remarks>
    /// Die laengste Strecke im Spiel liegt deutlich darunter. Die Grenze gibt es, weil
    /// eine offene Fahrt sonst unbegrenzt Messpunkte sammelt -- alle 5 m einen, und
    /// bei einer Stunde freiem Fahren waeren das Hunderttausende.
    /// </remarks>
    public const float MaxLapMetres = 60000f;

    /// <summary>Eine erkannte Ueberfahrt.</summary>
    /// <param name="Anchor">Die Linie, ueber die gefahren wurde.</param>
    /// <param name="Seconds">Wann -- in der Zeitrechnung, die <c>Step</c> bekommt.</param>
    /// <param name="HeadingX">Fahrtrichtung dabei, auf 1 normiert.</param>
    /// <param name="HeadingZ">Fahrtrichtung dabei, auf 1 normiert.</param>
    /// <param name="Distance">Wie nah die groesste Annaeherung war.</param>
    internal readonly record struct Pass(
        FreeRoamAnchor Anchor, float Seconds, float HeadingX, float HeadingZ, float Distance);

    private sealed class Beobachtung
    {
        public bool InZone;

        /// <summary>
        /// Diese Linie ist abgehakt, bis die Zone einmal verlassen wurde.
        /// </summary>
        /// <remarks>
        /// OHNE DIESEN RIEGEL MELDET EINE VORBEIFAHRT ZWEI UEBERFAHRTEN -- gemessen
        /// in der eigenen Pruefung, bevor er da war. Der Ablauf: die Annaeherung
        /// gilt als abgeschlossen, sobald man sich 25 m wieder entfernt hat. Dann
        /// steht man aber immer noch INNERHALB der 120-m-Zone, die Beobachtung
        /// beginnt sofort von neuem, und weil der Abstand weiter waechst, gilt auch
        /// diese zweite "Annaeherung" bald als abgeschlossen -- mit einem Minimum
        /// von 37 m, also knapp innerhalb der Annahmeschwelle.
        ///
        /// Fuer die Uhr waere das fatal: die zweite Meldung kaeme Sekundenbruchteile
        /// nach der ersten und wuerde die eben gestartete Runde sofort wieder
        /// zuruecksetzen.
        /// </remarks>
        public bool Gesperrt;

        public float MinAbstand;
        public float MinSekunden;
        public float MinX, MinZ;
        public float RichtungX, RichtungZ;
    }

    private readonly List<FreeRoamAnchor> _anker = new();
    private readonly Dictionary<FreeRoamAnchor, Beobachtung> _zustand = new();
    private float _letztesX, _letztesZ;
    private bool _hatLetztes;

    public IReadOnlyList<FreeRoamAnchor> Anchors => _anker;

    /// <summary>Eine Linie aufnehmen. Zu nahe an einer vorhandenen: dieselbe.</summary>
    public FreeRoamAnchor Add(FreeRoamAnchor anker)
    {
        var vorhanden = Nearest(anker.X, anker.Z, AcceptRadius);
        if (vorhanden is not null)
        {
            // Ein Name, der noch fehlt, darf nachgetragen werden -- mehr nicht. Der
            // ORT bleibt, wo er war: ein Anker, der bei jeder Aufnahme ein paar Meter
            // mitwandert, trifft nach hundert Aufnahmen die eigene Strecke nicht mehr.
            if (string.IsNullOrWhiteSpace(vorhanden.Name) && !string.IsNullOrWhiteSpace(anker.Name))
            {
                vorhanden.Name = anker.Name;
            }
            return vorhanden;
        }
        _anker.Add(anker);
        _zustand[anker] = new Beobachtung();
        return anker;
    }

    /// <summary>Die naechste Linie in Reichweite, oder null.</summary>
    public FreeRoamAnchor? Nearest(float x, float z, float within)
    {
        FreeRoamAnchor? beste = null;
        var besteEntfernung = float.MaxValue;
        foreach (var a in _anker)
        {
            var d = Abstand(a, x, z);
            if (d <= within && d < besteEntfernung) { besteEntfernung = d; beste = a; }
        }
        return beste;
    }

    private static float Abstand(FreeRoamAnchor a, float x, float z)
    {
        var dx = a.X - x;
        var dz = a.Z - z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public void Reset()
    {
        _hatLetztes = false;
        foreach (var b in _zustand.Values) { b.InZone = false; b.Gesperrt = false; }
    }

    /// <summary>
    /// Einen Messwert verarbeiten und melden, wenn dabei eine Linie ueberfahren wurde.
    /// </summary>
    /// <remarks>
    /// Gibt hoechstens eine Ueberfahrt je Aufruf zurueck -- die naechstgelegene, falls
    /// zwei Linien gleichzeitig ausloesen. Zwei Meldungen fuer einen Ort waeren fuer
    /// den Aufrufer nicht zu deuten.
    /// </remarks>
    public Pass? Step(float x, float z, float seconds)
    {
        float richtungX = 0f, richtungZ = 0f;
        if (_hatLetztes)
        {
            var dx = x - _letztesX;
            var dz = z - _letztesZ;
            var laenge = MathF.Sqrt(dx * dx + dz * dz);
            if (laenge > 0.001f) { richtungX = dx / laenge; richtungZ = dz / laenge; }
        }

        Pass? ergebnis = null;
        foreach (var a in _anker)
        {
            if (!_zustand.TryGetValue(a, out var b)) { continue; }
            var d = Abstand(a, x, z);

            if (!b.InZone)
            {
                // Draussen: der Riegel faellt, die naechste Annaeherung zaehlt wieder.
                if (d > ZoneRadius) { b.Gesperrt = false; continue; }
                if (b.Gesperrt) { continue; }
                b.InZone = true;
                b.MinAbstand = d;
                b.MinSekunden = seconds;
                b.MinX = x; b.MinZ = z;
                b.RichtungX = richtungX; b.RichtungZ = richtungZ;
                continue;
            }

            if (d < b.MinAbstand)
            {
                b.MinAbstand = d;
                b.MinSekunden = seconds;
                b.MinX = x; b.MinZ = z;
                if (richtungX != 0f || richtungZ != 0f)
                {
                    b.RichtungX = richtungX; b.RichtungZ = richtungZ;
                }
                continue;
            }

            // Wir entfernen uns wieder. Erst wenn genug Abstand dazwischen liegt --
            // oder die Zone verlassen ist -- steht fest, dass der naechste Punkt
            // hinter uns liegt.
            if (d < b.MinAbstand + LeaveHysteresis && d <= ZoneRadius) { continue; }

            b.InZone = false;
            b.Gesperrt = true;
            if (b.MinAbstand > AcceptRadius) { continue; }
            if (ergebnis is null || b.MinAbstand < ergebnis.Value.Distance)
            {
                ergebnis = new Pass(a, b.MinSekunden, b.RichtungX, b.RichtungZ, b.MinAbstand);
            }
        }

        // ZEITLICHE AUFLOESUNG: ein Paket, also rund 17 ms bei 60 Hz. Zwischen den
        // Paketen wird NICHT interpoliert. Der Punkt der groessten Annaeherung liegt
        // im Scheitel einer Parabel, der Fehler ist dort also von zweiter Ordnung --
        // bei 250 km/h und 1,2 m Paketabstand bleiben Millisekunden. Fuer eine
        // Rundenzeit, die auf Zehntel verglichen wird, ist das ohne Belang, und eine
        // Interpolation waere Genauigkeit, die die Quelle nicht hergibt.
        _letztesX = x; _letztesZ = z; _hatLetztes = true;
        return ergebnis;
    }

    // ------------------------------------------------------------------ //
    // Woher die Linien kommen
    // ------------------------------------------------------------------ //

    /// <summary>Die von Hand gesetzten Linien.</summary>
    public static string LinesPath => Path.Combine(AppInfo.DataFolder, "freeroam_lines.json");

    /// <summary>
    /// Alle bekannten Start-Ziel-Linien einlesen.
    /// </summary>
    /// <remarks>
    /// Zwei Quellen, und die erste ist die wichtige: JEDE je aufgezeichnete Strecke
    /// hat schon eine Linie -- sie steht als <c>startX/startZ</c> in ihrer
    /// <c>course.json</c>. Wer eine Rivalen-Strecke einmal gefahren ist, kann sie
    /// darum ohne weiteres Zutun im freien Fahren ueben, und die Zeit ist mit der aus
    /// dem Rennen vergleichbar, weil beide an derselben Linie beginnen.
    ///
    /// Das gilt ausdruecklich auch fuer EventLab-Strecken: die Zuordnung ist rein
    /// geometrisch und fragt das Spiel nie nach einem Streckennamen.
    /// </remarks>
    public static FreeRoamTimer Load(string? lapsRoot = null, string? linesPath = null)
    {
        var timer = new FreeRoamTimer();
        timer.LoadCourses(lapsRoot ?? LapArchive.Root);
        timer.LoadLines(linesPath ?? LinesPath);
        return timer;
    }

    public void LoadCourses(string wurzel)
    {
        try
        {
            if (!Directory.Exists(wurzel)) { return; }
            foreach (var ordner in LapArchive.KursOrdner(wurzel))
            {
                var notiz = Path.Combine(ordner, "course.json");
                if (!File.Exists(notiz)) { continue; }
                LapArchive.CourseNote? gelesen;
                try
                {
                    gelesen = JsonSerializer.Deserialize<LapArchive.CourseNote>(
                        File.ReadAllText(notiz));
                }
                catch (Exception) { continue; }
                if (gelesen is null) { continue; }
                if (gelesen.StartX == 0f && gelesen.StartZ == 0f) { continue; }
                Add(new FreeRoamAnchor
                {
                    Name = string.IsNullOrWhiteSpace(gelesen.Name)
                        ? LapArchive.KennungAus(Path.GetFileName(ordner)) ?? Path.GetFileName(ordner)
                        : gelesen.Name,
                    X = gelesen.StartX,
                    Z = gelesen.StartZ,
                    Source = "archive",
                });
            }
        }
        catch (Exception)
        {
            // Ohne Linien laeuft die Uhr eben nicht -- das ist kein Grund abzustuerzen.
        }
    }

    public void LoadLines(string pfad)
    {
        try
        {
            if (!File.Exists(pfad)) { return; }
            var gelesen = JsonSerializer.Deserialize<List<FreeRoamAnchor>>(
                File.ReadAllText(pfad));
            if (gelesen is null) { return; }
            foreach (var a in gelesen)
            {
                if (a.X == 0f && a.Z == 0f) { continue; }
                a.Source = "manual";
                Add(a);
            }
        }
        catch (Exception)
        {
            // dito
        }
    }

    /// <summary>Die von Hand gesetzten Linien sichern.</summary>
    public void SaveLines(string? pfad = null)
    {
        try
        {
            var ziel = pfad ?? LinesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(ziel)!);
            var eigene = _anker.Where(a => a.Source == "manual").ToList();
            var temp = ziel + ".part";
            File.WriteAllText(temp, JsonSerializer.Serialize(
                eigene, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, ziel, overwrite: true);
        }
        catch (Exception)
        {
            // Eine verlorene Linie ist aergerlich, ein Absturz im Spiel mehr.
        }
    }
}
