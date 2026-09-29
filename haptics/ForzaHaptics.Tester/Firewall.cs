using System.ComponentModel;
using System.Diagnostics;

namespace ForzaHaptics.Tester;

/// <summary>
/// Laesst die Windows-Firewall die Telemetrie der Xbox herein? (seit 2026-09-29)
/// </summary>
/// <remarks>
/// Im Modus "Xbox / 2nd PC" horcht die App im Netz auf UDP. Windows fragt beim ersten
/// Mal selbst nach -- wer dort "Abbrechen" klickt, bekommt eine Sperrregel fuer das
/// Programm, und von der Xbox kommt nie ein Paket an. Ohne Hinweis sieht das aus wie
/// eine falsch eingetragene Adresse.
///
/// Gelesen wird ueber die Firewall-Schnittstelle von Windows (HNetCfg.FwPolicy2), das
/// geht ohne Adminrechte. Eine Regel ANLEGEN braucht sie: das geschieht nur auf Knopfdruck,
/// mit der Rueckfrage von Windows, und nur fuer den Telemetrie-Port.
/// </remarks>
internal static class Firewall
{
    internal const string RegelName = "FH Companion telemetry (UDP)";

    internal enum Stand { Unbekannt, Offen, Fehlt, Blockiert }

    /// <summary>Was die Firewall zum Port sagt, und ob gerade ein oeffentliches Netz aktiv ist.</summary>
    internal static (Stand Stand, bool Oeffentlich) Pruefen(int port, string programm)
    {
        (Stand, bool) ergebnis = (Stand.Unbekannt, false);
        // Auf einem eigenen STA-Faden: die Schnittstelle ist ein COM-Objekt.
        var faden = new Thread(() => ergebnis = PruefenCom(port, programm)) { IsBackground = true };
        faden.SetApartmentState(ApartmentState.STA);
        faden.Start();
        return faden.Join(TimeSpan.FromSeconds(20)) ? ergebnis : (Stand.Unbekannt, false);
    }

    private static (Stand, bool) PruefenCom(int port, string programm)
    {
        try
        {
            var typ = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (typ is null) { return (Stand.Unbekannt, false); }
            dynamic policy = Activator.CreateInstance(typ)!;
            int profile = (int)policy.CurrentProfileTypes;
            var regeln = new List<Regel>();
            foreach (dynamic r in policy.Rules)
            {
                try
                {
                    string? app = r.ApplicationName;
                    string? ports = r.LocalPorts;
                    regeln.Add(new Regel((bool)r.Enabled, (int)r.Direction, (int)r.Profiles, (int)r.Protocol,
                                         (int)r.Action, app, ports));
                }
                catch (Exception)
                {
                }
            }
            return (Bewerten(regeln, profile, port, programm), (profile & 4) != 0);
        }
        catch (Exception)
        {
            return (Stand.Unbekannt, false);
        }
    }

    /// <summary>Eine Firewall-Regel, soweit sie hier zaehlt (Richtung 1 = herein, Aktion 1 = erlauben).</summary>
    internal readonly record struct Regel(bool An, int Richtung, int Profile, int Protokoll, int Aktion,
                                          string? Programm, string? Ports);

    /// <summary>Die Regel ohne Windows -- fuer den Test.</summary>
    /// <remarks>
    /// Sperrt eine Regel das Programm oder den Port, gewinnt sie (so haelt es Windows).
    /// Erlaubt ist, was fuer den Port oder fuer genau dieses Programm UDP hereinlaesst,
    /// in einem der aktiven Profile. Regeln anderer Programme zaehlen nicht.
    /// </remarks>
    internal static Stand Bewerten(IEnumerable<Regel> regeln, int aktiveProfile, int port, string programm)
    {
        bool erlaubt = false, gesperrt = false;
        foreach (var r in regeln)
        {
            if (!r.An || r.Richtung != 1 || (r.Profile & aktiveProfile) == 0) { continue; }
            if (r.Protokoll != 17 && r.Protokoll != 256) { continue; }
            var unseres = r.Programm is { Length: > 0 } p && Gleich(p, programm);
            if (r.Programm is { Length: > 0 } && !unseres) { continue; }
            if (!PortPasst(r.Ports, port)) { continue; }
            if (r.Aktion == 0) { gesperrt = true; }
            else if (r.Aktion == 1) { erlaubt = true; }
        }
        return gesperrt ? Stand.Blockiert : erlaubt ? Stand.Offen : Stand.Fehlt;
    }

    private static bool Gleich(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    internal static bool PortPasst(string? ports, int port)
    {
        if (string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*") { return true; }
        foreach (var teil in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bereich = teil.Split('-');
            if (bereich.Length == 2 && int.TryParse(bereich[0], out var von) && int.TryParse(bereich[1], out var bis)
                && port >= von && port <= bis) { return true; }
            if (int.TryParse(teil, out var einzeln) && einzeln == port) { return true; }
        }
        return false;
    }

    /// <summary>Der Befehl, den <see cref="Anlegen"/> mit Adminrechten ausfuehrt -- fuer den Test.</summary>
    internal static string Befehl(int port, bool oeffentlich, bool sperreWeg, string programm)
    {
        var profile = oeffentlich ? "private,domain,public" : "private,domain";
        var teile = new List<string>();
        if (sperreWeg)
        {
            // Die Sperre, die Windows nach einem "Abbrechen" anlegt, gilt fuer das Programm.
            teile.Add($"netsh advfirewall firewall delete rule name=all dir=in program=\"{programm}\"");
        }
        teile.Add($"netsh advfirewall firewall delete rule name=\"{RegelName}\"");
        teile.Add($"netsh advfirewall firewall add rule name=\"{RegelName}\" dir=in action=allow protocol=UDP localport={port} profile={profile}");
        return "/c " + string.Join(" & ", teile);
    }

    /// <summary>
    /// Die Regel anlegen: mit der Rueckfrage von Windows (Adminrechte), nur auf Knopfdruck.
    /// Gibt zurueck, ob es geklappt hat; eine abgelehnte Rueckfrage ist kein Fehler.
    /// </summary>
    internal static bool Anlegen(int port, bool oeffentlich, bool sperreWeg, string programm)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", Befehl(port, oeffentlich, sperreWeg, programm))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null) { return false; }
            p.WaitForExit(30000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;   // Rueckfrage abgelehnt
        }
        catch (Exception)
        {
            return false;
        }
    }
}
