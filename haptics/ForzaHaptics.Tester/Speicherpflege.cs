using System.Runtime;

namespace ForzaHaptics.Tester;

/// <summary>
/// DER SPEICHER WUCHS UEBER STUNDEN (seit 2026-11-14 gemessen): nach vier Stunden 3,8 GB privat,
/// davon 1,66 GB im Large Object Heap und 1,34 GB davon Luecken -- grosse Puffer (Telemetriespuren,
/// gepackte Runden, der Datensatz) entstehen je Runde neu, und der GC schiebt diesen Bereich von
/// sich aus nicht zusammen. GCConserveMemory (im Projekt) tut es, wenn es schlimm wird; hier
/// zusaetzlich alle zehn Minuten, sobald mehr als 200 MB Luecke darin steht.
/// </summary>
internal static class Speicherpflege
{
    private static System.Threading.Timer? _uhr;

    public static void Starten()
    {
        _uhr ??= new System.Threading.Timer(_ => Pflegen(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    internal static void Pflegen()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            var gen = info.GenerationInfo;
            var luecke = gen.Length > 3 ? gen[3].FragmentationAfterBytes : 0;
            if (luecke < 200L * 1024 * 1024) { return; }
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch (Exception)
        {
            // Pflege darf nie stoeren.
        }
    }
}
