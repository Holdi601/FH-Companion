using System.Reflection;

namespace ForzaHaptics.Tester;

/// <summary>
/// Symbol und Logo der App, als Ressourcen in der DLL.
/// </summary>
/// <remarks>
/// Icon.ExtractAssociatedIcon liefert aus der Programmdatei nur das 32-Pixel-Bild:
/// die Titelleiste verkleinert es, der Kopf des Hauptfensters blies es auf 56 Pixel
/// auf, unscharf. Die ICO-Datei traegt alle Groessen von 16 bis 256 (bis 48 eine
/// vereinfachte Fassung, darueber das volle Schild), das Logo liegt als 256-Pixel-PNG
/// daneben -- in der dunklen Fassung, weil das Fenster dunkel ist.
///
/// Fehlt eine Ressource, gibt es null, und der Aufrufer faellt auf das alte Verfahren
/// zurueck: ein fehlendes Bild darf die App nicht am Start hindern.
/// </remarks>
internal static class Marke
{
    public const string SymbolName = "FhCompanion.icon.ico";
    public const string LogoName = "FhCompanion.logo-dark.png";

    public static Icon? Symbol() => Laden(SymbolName, s => new Icon(s));

    public static Image? Logo() => Laden(LogoName, s => Image.FromStream(s));

    private static T? Laden<T>(string name, Func<Stream, T> bauen) where T : class
    {
        try
        {
            using var roh = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (roh is null) { return null; }
            // Eine eigene Kopie: Image.FromStream braucht den Strom, solange das Bild lebt.
            var kopie = new MemoryStream();
            roh.CopyTo(kopie);
            kopie.Position = 0;
            return bauen(kopie);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
