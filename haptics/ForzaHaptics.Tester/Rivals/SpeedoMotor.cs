using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// DER TACHO AUF SEINEM EIGENEN FADEN (seit 2026-10-18).
/// </summary>
/// <remarks>
/// ## Warum
///
/// Der Nutzer: "das rpm ding so hakelig ... die Pakete werden mit der fps rate des Bildschirms
/// gesendet". Gemessen: rund 110 Pakete je Sekunde. Der Tacho bekam davon zehn -- die App reichte
/// die Telemetrie nur alle 100 ms an die Anzeigen weiter -- und zeichnete auf dem Faden der
/// Oberflaeche, im Wettbewerb mit allem anderen, und gedrosselt nach Paketankunft (30 Bilder je
/// Sekunde gegen 60 Pakete ergaben Abstaende von 33 und 50 ms im Wechsel: Ruckeln).
///
/// ## Wie
///
/// * Der Empfangsfaden reicht JEDES Paket sofort herein (<see cref="Paket"/>), ohne die Oberflaeche.
/// * Dieser Faden hat sein eigenes Fenster und seine eigene Nachrichtenschleife. Er nimmt alle
///   Pakete auf (Strecke, Schaltpunkt und Lampen brauchen jedes), glaettet die Anzeige nach ZEIT
///   statt je Paket und zeichnet im Takt des Bildschirms (DwmFlush wartet auf das naechste Bild)
///   -- nur, wenn sich etwas sichtbar geaendert hat oder eine Animation laeuft.
/// * Alles andere -- Design, Lage, Vorschau, sichtbar oder nicht -- kommt als Auftrag herein und
///   wird hier erledigt. Nur dieser Faden fasst das Fenster an.
/// </remarks>
internal sealed class SpeedoMotor : IDisposable
{
    /// <summary>Der laufende Motor, fuer den Empfangsfaden. null: keiner.</summary>
    public static SpeedoMotor? Aktiv { get; private set; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private readonly OverlaySettings _settings;
    private readonly Thread _faden;
    private readonly ConcurrentQueue<(ForzaPacket Paket, DateTime Zeit)> _pakete = new();
    private readonly ConcurrentQueue<Action> _auftraege = new();
    private readonly AutoResetEvent _wecker = new(false);
    private readonly SpeedoState _daten;
    private volatile bool _ende;
    private volatile bool _vorne = true;
    private volatile bool _vorschau;
    private SpeedoHud? _hud;
    private SpeedoDesign _design;
    // DAS G-METER (seit 2026-10-24) laeuft auf demselben Faden, im selben Takt.
    private GMeterHud? _gHud;
    private GMeterDesign _gDesign;
    private readonly GMeterState _gDaten = new();
    private readonly object _liveSperre = new();
    private (int Auto, SpeedoMesswerte Werte) _live;

    public SpeedoMotor(OverlaySettings settings, Rectangle flaeche, bool merken = true)
    {
        _settings = settings;
        _design = SpeedoDesigns.Finde(settings.SpeedoDesign);
        _gDesign = GMeterDesigns.Finde(settings.GMeterDesign);
        _daten = new SpeedoState(merken);
        _faden = new Thread(() => Lauf(flaeche)) { IsBackground = true, Name = "Tacho" };
        _faden.SetApartmentState(ApartmentState.STA);
        _faden.Start();
        Aktiv = this;
    }

    // ------------------------------------------------------------------ //
    // Von aussen -- jeder Faden
    // ------------------------------------------------------------------ //

    /// <summary>Ein Paket, vom Empfangsfaden. Staut sich etwas (Faden haengt), faellt das Aelteste weg.</summary>
    public void Paket(ForzaPacket p)
    {
        _pakete.Enqueue((p, DateTime.UtcNow));
        while (_pakete.Count > 600 && _pakete.TryDequeue(out _)) { }
        _wecker.Set();
    }

    /// <summary>Ist das Spiel vorn (bzw. darf ueberhaupt etwas ueber ihm liegen)?</summary>
    public void Vorne(bool vorne)
    {
        if (_vorne == vorne) { return; }
        _vorne = vorne;
        _wecker.Set();
    }

    /// <summary>Die Einrichtung zeigt alle Anzeigen zur Probe.</summary>
    public void Vorschau(bool an)
    {
        _vorschau = an;
        Auftrag(() => _neuZeichnen = true);
    }

    /// <summary>Das gewaehlte Design neu lesen -- oder dieses nehmen.</summary>
    public void Design(SpeedoDesign? design) => Auftrag(() =>
    {
        SpeedoDesigns.Vergessen();
        _design = design ?? SpeedoDesigns.Finde(_settings.SpeedoDesign);
        _hud?.DesignNeu(_design);
        _neuZeichnen = true;
    });

    /// <summary>Das G-Meter: Design neu lesen -- oder dieses nehmen.</summary>
    public void GDesign(GMeterDesign? design) => Auftrag(() =>
    {
        GMeterDesigns.Vergessen();
        _gDesign = design ?? GMeterDesigns.Finde(_settings.GMeterDesign);
        _gHud?.DesignNeu(_gDesign);
        _neuZeichnen = true;
    });

    /// <summary>Spitzen, Huelle und Waermekarte des G-Meters auf null.</summary>
    public void GZuruecksetzen() => Auftrag(() => { _gDaten.Zuruecksetzen(); _neuZeichnen = true; });

    /// <summary>Beim naechsten Bild neu zeichnen (Lage, Groesse, Einheiten geaendert).</summary>
    public void NeuZeichnen() => Auftrag(() => _neuZeichnen = true);

    public void Flaeche(Rectangle flaeche) => Auftrag(() => { _hud?.SetArea(flaeche); _gHud?.SetArea(flaeche); _neuZeichnen = true; });

    public void TripZurueck() => Auftrag(() => _daten.TripZurueck());

    /// <summary>Welches Auto faehrt und was die Lampen messen -- fuer den Reiter.</summary>
    public (int Auto, SpeedoMesswerte Werte) Live
    {
        get { lock (_liveSperre) { return _live; } }
    }

    private void Auftrag(Action a)
    {
        _auftraege.Enqueue(a);
        _wecker.Set();
    }

    public void Dispose()
    {
        if (ReferenceEquals(Aktiv, this)) { Aktiv = null; }
        _ende = true;
        _wecker.Set();
        try { _faden.Join(2000); } catch (Exception) { }
        _wecker.Dispose();
    }

    // ------------------------------------------------------------------ //
    // Der Faden
    // ------------------------------------------------------------------ //

    private bool _neuZeichnen = true;
    private float _rpmAnzeige;
    private float _tempoAnzeige;
    private int _zeichen;
    private DateTime _letztesBild = DateTime.MinValue;

    private void Lauf(Rectangle flaeche)
    {
        try
        {
            _hud = new SpeedoHud(_settings, flaeche);
            _hud.DesignNeu(_design);
            _gHud = new GMeterHud(_settings, flaeche);
            _gHud.DesignNeu(_gDesign);
            var uhr = Stopwatch.StartNew();
            var zuletzt = uhr.Elapsed.TotalSeconds;
            SpeedoBild? roh = null;
            var paketZeit = DateTime.MinValue;
            var rennt = false;
            while (!_ende)
            {
                Application.DoEvents();
                while (_auftraege.TryDequeue(out var a))
                {
                    try { a(); } catch (Exception) { }
                }

                // ALLE Pakete aufnehmen: Strecke, Schaltpunkt und Lampen brauchen jedes.
                var neu = false;
                while (_pakete.TryDequeue(out var e))
                {
                    var auto = (int)e.Paket.Get("CarOrdinal");
                    roh = _daten.Aufnehmen(e.Paket, 0f, _design, SpeedoSchwellen.Fuer(_settings, auto), e.Zeit);
                    _gDaten.Aufnehmen(e.Paket, e.Zeit);
                    paketZeit = e.Zeit;
                    rennt = e.Paket.Get("IsRaceOn") >= 0.5;
                    neu = true;
                }
                if (neu)
                {
                    lock (_liveSperre) { _live = (_daten.Auto, _daten.Messwerte); }
                }

                var jetzt = uhr.Elapsed.TotalSeconds;
                var dt = (float)Math.Clamp(jetzt - zuletzt, 0.0, 0.25);
                zuletzt = jetzt;

                var frisch = DateTime.UtcNow - paketZeit < TimeSpan.FromSeconds(1.5);
                var faehrt = rennt && _vorne && frisch && roh is not null;
                // Ein Bildschirmbild hoechstens so oft wie eingestellt (0 = so oft der Schirm kann).
                var fps = _settings.SpeedoFps;
                var faellig = fps <= 0 || (DateTime.UtcNow - _letztesBild).TotalSeconds >= 1.0 / Math.Clamp(fps, 10, 1000) - 0.0005;
                var gezeichnet = false;
                if (faellig)
                {
                    gezeichnet |= Tacho(faehrt, roh, paketZeit, jetzt, dt);
                    gezeichnet |= GMeter(faehrt, neu, paketZeit, jetzt, dt);
                }
                if (gezeichnet)
                {
                    _neuZeichnen = false;
                    _letztesBild = DateTime.UtcNow;
                    // Bis zum naechsten Bild des Schirms warten -- der natuerliche Takt.
                    if (DwmFlush() != 0) { Thread.Sleep(4); }
                }
                else
                {
                    var irgendwas = _settings.HudSpeedo || _settings.HudGMeter;
                    // Nichts zu tun: auf das naechste Paket warten -- sichtbar kurz, unsichtbar laenger.
                    _wecker.WaitOne(_hud.Visible || _gHud.Visible ? 4 : irgendwas ? 50 : 250);
                }
            }
        }
        catch (Exception x)
        {
            OverlayController.WriteDiagnostic("Tacho-Faden: " + x.Message);
        }
        finally
        {
            try { _daten.Speichern(); } catch (Exception) { }
            try { _hud?.Close(); _hud?.Dispose(); } catch (Exception) { }
            try { _gHud?.Close(); _gHud?.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>Den Tacho zeigen, aendern oder verstecken. true = es wurde gezeichnet.</summary>
    private bool Tacho(bool faehrt, SpeedoBild? roh, DateTime paketZeit, double jetzt, float dt)
    {
        var hud = _hud!;
        if (!_settings.HudSpeedo || !(_vorschau || faehrt))
        {
            if (hud.Visible) { hud.Hide(); _daten.Speichern(); }
            return false;
        }
        SpeedoBild bild;
        if (!faehrt)
        {
            bild = SpeedoState.Vorfuehrung(0, ruhig: true);
        }
        else
        {
            // NACH ZEIT GLAETTEN, nicht je Paket: so laeuft die Nadel bei jeder Paketrate und jeder
            // Bildrate gleich. Zeitkonstante aus "Needle smoothing": 0 = roh, 0,5 = 37 ms, 0,95 = 135 ms.
            var r = roh!.Value;
            var tau = _design.Smoothing * _design.Smoothing * 0.15f;
            var k = tau <= 0.001f ? 1f : 1f - (float)Math.Exp(-dt / tau);
            _rpmAnzeige += (r.Rpm - _rpmAnzeige) * k;
            _tempoAnzeige += (r.SpeedMs - _tempoAnzeige) * k;
            if (Math.Abs(r.Rpm - _rpmAnzeige) < 1f) { _rpmAnzeige = r.Rpm; }
            var alter = (float)(DateTime.UtcNow - paketZeit).TotalSeconds;
            bild = r with { Rpm = _rpmAnzeige, SpeedMs = _tempoAnzeige, SeitGangwechsel = r.SeitGangwechsel + alter, Uhr = jetzt };
        }
        var zeichen = Zeichen(bild, _design);
        var bewegt = (_design.GearPop && bild.SeitGangwechsel < 0.3f)
                     || (bild.ImRoten && (_design.RedlinePulse || _design.ShowShiftLights))
                     || (bild.AtLimiter && _design.LimiterFlash)
                     || (bild.Stab == 2 && _design.ShowStability)
                     || (bild.Rat != 0 && _design.ShowShiftAdvice)
                     || Math.Abs(bild.Rpm - (roh?.Rpm ?? bild.Rpm)) >= 1f;
        if (!_neuZeichnen && zeichen == _zeichen && !bewegt && hud.Visible) { return false; }
        hud.Setze(bild);
        if (!hud.Visible) { hud.Show(); hud.TopMost = true; }
        _zeichen = zeichen;
        return true;
    }

    private float _gx, _gz, _gy;
    private bool _gBewegt;

    /// <summary>Das G-Meter zeigen, aendern oder verstecken. true = es wurde gezeichnet.</summary>
    private bool GMeter(bool faehrt, bool neu, DateTime paketZeit, double jetzt, float dt)
    {
        var hud = _gHud!;
        if (!_settings.HudGMeter || !(_vorschau || faehrt))
        {
            if (hud.Visible) { hud.Hide(); }
            return false;
        }
        GMeterBild bild;
        if (!faehrt)
        {
            if (!_neuZeichnen && hud.Visible) { return false; }
            bild = GMeterState.Vorfuehrung(0, ruhig: true);
        }
        else
        {
            var tau = _gDesign.Smoothing * _gDesign.Smoothing * 0.2f;
            var k = tau <= 0.001f ? 1f : 1f - (float)Math.Exp(-dt / tau);
            _gx += (_gDaten.X - _gx) * k;
            _gz += (_gDaten.Z - _gz) * k;
            _gy += (_gDaten.Y - _gy) * k;
            var wandert = MathF.Abs(_gDaten.X - _gx) + MathF.Abs(_gDaten.Z - _gz) + MathF.Abs(_gDaten.Y - _gy) > 0.002f;
            // Neue Pakete, eine Spur, die verblasst, oder ein Punkt, der noch wandert: zeichnen.
            if (!neu && !wandert && !_gBewegt && !_neuZeichnen && hud.Visible) { return false; }
            _gBewegt = wandert || (_gDesign.Trail != GMeterTrail.Off && DateTime.UtcNow - paketZeit < TimeSpan.FromSeconds(_gDesign.TrailSeconds))
                       || _gDesign.Layout == GMeterLayout.Radar;
            bild = _gDaten.Bild(_gx, _gz, _gy, _gDesign.TrailSeconds, DateTime.UtcNow, jetzt);
        }
        hud.Setze(bild);
        if (!hud.Visible) { hud.Show(); hud.TopMost = true; }
        return true;
    }

    /// <summary>Was im Bild sichtbar ist, als Zahl -- gleich heisst: nicht neu zeichnen.</summary>
    internal static int Zeichen(SpeedoBild b, SpeedoDesign d)
    {
        var h = new HashCode();
        h.Add((int)(b.Rpm / 10f));
        h.Add((int)(b.SpeedMs * 3.6f));
        h.Add(b.Gear);
        h.Add(b.Wheelspin);
        h.Add(b.WheelLock);
        h.Add(b.AtLimiter);
        h.Add(b.Stab);
        h.Add(b.Rat);
        h.Add(b.BesterGang);
        if (d.ShowPower) { h.Add((int)(b.PowerW / 2000f)); }
        if (d.ShowBoost) { h.Add((int)(b.BoostPsi * 10f)); }
        if (d.ShowPedals) { h.Add((int)(b.Throttle * 50f)); h.Add((int)(b.Brake * 50f)); }
        if (d.Distance != SpeedoDistance.Off) { h.Add((int)((b.TripM + b.RaceM + b.OdometerM) / 50f)); }
        h.Add(d.Clock switch
        {
            SpeedoClock.RaceTime => (int)(b.RaceTime * 10f),
            SpeedoClock.LapTime => (int)(b.LapTime * 10f),
            SpeedoClock.Off => 0,
            _ => DateTime.Now.Minute,
        });
        return h.ToHashCode();
    }
}
