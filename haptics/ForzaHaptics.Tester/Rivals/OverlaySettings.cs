using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// The overlay's settings, in the same <c>config/overlay.json</c> the tooling uses.
/// </summary>
/// <remarks>
/// One file, not two. `scripts/screen_reader.py --boxes` is how the mask fractions
/// get checked against a real capture, and if this app kept its own copy of them the
/// calibration would be done against numbers the overlay does not use.
///
/// Unknown keys are preserved on save (the Python side has a few this app does not
/// read), so writing from here never silently drops them.
/// </remarks>
internal sealed class OverlaySettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; } = new();

    [JsonPropertyName("score_mode")] public string ScoreMode { get; set; } = "points";
    [JsonPropertyName("show_seconds")] public double ShowSeconds { get; set; } = 30;

    /// <summary>
    /// Lief das Overlay, als die App zuletzt beendet wurde? Dann startet es wieder.
    /// </summary>
    /// <remarks>
    /// Seit 2026-09-26: ein Update startet die App neu, ohne "--overlay" -- danach war
    /// das Overlay aus, und der Nutzer fand die Streckenkarten nicht mehr.
    /// </remarks>
    [JsonPropertyName("overlay_running")] public bool OverlayRunning { get; set; }
    [JsonPropertyName("auto_show")] public bool AutoShow { get; set; } = true;
    [JsonPropertyName("poll_seconds")] public double PollSeconds { get; set; } = 1.5;

    [JsonPropertyName("hotkey_right_vk")] public int HotkeyRight { get; set; } = 119;   // F8
    [JsonPropertyName("hotkey_left_vk")] public int HotkeyLeft { get; set; } = 117;     // F6
    [JsonPropertyName("hotkey_score_vk")] public int HotkeyScore { get; set; } = 118;   // F7
    [JsonPropertyName("hotkey_pin_vk")] public int HotkeyPin { get; set; } = 120;       // F9
    // Page Down / Page Up: the game does not use them in its menus, and they are
    // the keys a list already means. Set either to 0 to give the key back.
    [JsonPropertyName("hotkey_scroll_down_vk")] public int HotkeyScrollDown { get; set; } = 34;
    [JsonPropertyName("hotkey_scroll_up_vk")] public int HotkeyScrollUp { get; set; } = 33;
    // F11 blaettert durch die Vergleichsstufen des Deltas.
    [JsonPropertyName("hotkey_delta_mode_vk")] public int HotkeyDeltaMode { get; set; } = 122;

    /// <summary>
    /// Hier eine Start-Ziel-Linie fuer die freie Welt setzen. F10.
    /// </summary>
    /// <remarks>
    /// EINE TASTE UND KEIN KNOPF. Wer eine Stelle der Landschaft zur Startlinie
    /// machen will, ist in diesem Moment dort -- mit 200 km/h. Ein Knopf im Fenster
    /// waere zum Umschalten da und die Stelle dabei laengst vorbei.
    /// </remarks>
    [JsonPropertyName("hotkey_free_line_vk")] public int HotkeyFreeRoamLine { get; set; } = 121;

    /// <summary>Den Delta-Streifen oben ueberhaupt zeigen.</summary>
    [JsonPropertyName("delta_hud")] public bool DeltaHud { get; set; } = true;

    /// <summary>
    /// Woran sich das Delta misst.
    /// </summary>
    /// <remarks>
    /// "tune" dieses Auto in dieser Abstimmung, "car" dieses Auto, "class" diese
    /// Leistungsklasse, "carclass" dieses Auto in dieser Klasse, "any" die eigene
    /// Bestzeit ueberhaupt -- und "dual" zeigt ZWEI Zahlen: oben dasselbe Auto auf
    /// dieser Strecke, darunter dieselbe Leistungsklasse auf derselben Strecke.
    /// </remarks>
    [JsonPropertyName("delta_reference")] public string DeltaReferenceMode { get; set; } = "tune";

    // Lage und Groesse der drei Anzeigen, als Anteil der Bildschirmbreite und -hoehe.
    // 0,5 / 0,02 mit "center" heisst: oben mittig. So laesst sich jedes Element
    // verschieben, ohne den Quelltext anzufassen.
    [JsonPropertyName("hud_delta_x")] public double HudDeltaX { get; set; } = 0.5;
    [JsonPropertyName("hud_delta_y")] public double HudDeltaY { get; set; } = 0.015;
    [JsonPropertyName("hud_delta_align")] public string HudDeltaAlign { get; set; } = "center";
    [JsonPropertyName("hud_delta_scale")] public double HudDeltaScale { get; set; } = 1.0;

    // NICHT oben links: dort schreibt das Spiel SELBST Rundenzeit und Fortschritt
    // (2026-09-12 vom Nutzer bemerkt -- mein Countdown stand darueber). Der Geist
    // sitzt darum mittig unter dem Delta, wo im Rennen nichts liegt.
    [JsonPropertyName("hud_ghost_x")] public double HudGhostX { get; set; } = 0.5;
    [JsonPropertyName("hud_ghost_y")] public double HudGhostY { get; set; } = 0.16;
    [JsonPropertyName("hud_ghost_align")] public string HudGhostAlign { get; set; } = "center";
    [JsonPropertyName("hud_ghost_scale")] public double HudGhostScale { get; set; } = 1.0;

    // Die Eingabespuren: Gas, Bremse, Kupplung, Lenkung -- jetzt gegen die Bestzeit.
    // Ausgeschaltet in der Vorgabe: vier mitlaufende Kurven sind fuer viele zu viel,
    // und was standardmaessig auf dem Schirm liegt, muss man erst wieder loswerden.
    [JsonPropertyName("hud_inputs")] public bool HudInputs { get; set; }
    [JsonPropertyName("hud_inputs_x")] public double HudInputsX { get; set; } = 0.02;
    [JsonPropertyName("hud_inputs_y")] public double HudInputsY { get; set; } = 0.55;
    [JsonPropertyName("hud_inputs_align")] public string HudInputsAlign { get; set; } = "left";
    [JsonPropertyName("hud_inputs_scale")] public double HudInputsScale { get; set; } = 1.0;

    /// <summary>Wie viele Sekunden die Spuren zurueckreichen.</summary>
    [JsonPropertyName("hud_inputs_seconds")] public double HudInputsSeconds { get; set; } = 6;

    /// <summary>
    /// Wie weit die Spur der Bestzeit VOR den jetzigen Punkt hinausreicht.
    /// </summary>
    /// <remarks>
    /// Der eigentliche Nutzen: was gleich kommt, laesst sich nachfahren; was vorbei
    /// ist, nur noch bedauern. Nur die Referenz hat eine Zukunft -- die eigene Spur
    /// endet zwangslaeufig am jetzigen Punkt.
    /// </remarks>
    [JsonPropertyName("hud_inputs_lookahead")] public double HudInputsLookahead { get; set; } = 1.5;

    /// <summary>
    /// Wie dick die Linien der Spuren sind -- 1,0 ist die bisherige Staerke.
    /// </summary>
    /// <remarks>
    /// Ein Faktor und keine Pixelzahl: die Spuren richten sich nach der Schirmhoehe,
    /// damit sie auf 1080p und auf 4K gleich gross aussehen. Eine feste Pixelzahl
    /// waere auf dem einen ein Haar und auf dem anderen ein Balken.
    /// </remarks>
    [JsonPropertyName("hud_inputs_line_width")] public double HudInputsLineWidth { get; set; } = 1.0;

    [JsonPropertyName("hud_color_mine")] public string HudColorMine { get; set; } = "#e7ecf3";
    [JsonPropertyName("hud_color_theirs")] public string HudColorTheirs { get; set; } = "#ffb454";
    [JsonPropertyName("hud_color_now")] public string HudColorNow { get; set; } = "#7ee787";

    [JsonIgnore]
    public HudPlacement InputsPlacement => new(
        (float)HudInputsX, (float)HudInputsY, HudInputsAlign ?? "left",
        (float)HudInputsScale);

    [JsonPropertyName("hud_note_x")] public double HudNoteX { get; set; } = 0.02;
    [JsonPropertyName("hud_note_y")] public double HudNoteY { get; set; } = 0.90;
    [JsonPropertyName("hud_note_align")] public string HudNoteAlign { get; set; } = "left";
    [JsonPropertyName("hud_note_scale")] public double HudNoteScale { get; set; } = 1.0;

    // ---- Streckenumrisse vor dem Rennen ------------------------------- //
    //
    // Der Anmeldeschirm nennt drei Strecken beim Namen. Ein Name sagt einem aber
    // nur etwas, wenn man die Strecke kennt -- die FORM sagt es sofort. Gezeichnet
    // wird sie aus den eigenen Runden (siehe CourseShape), weil das auch
    // EventLab-Strecken und selbstgebaute Kurse erfasst, die im Rivalen-Menue des
    // Spiels gar nicht vorkommen.
    [JsonPropertyName("course_shapes")] public bool CourseShapes { get; set; } = true;

    /// <summary>"telemetry", "rivals" oder "auto".</summary>
    [JsonPropertyName("course_shape_source")]
    public string CourseShapeSource { get; set; } = "auto";

    [JsonPropertyName("hud_course_x")] public double HudCourseX { get; set; } = 0.78;
    [JsonPropertyName("hud_course_y")] public double HudCourseY { get; set; } = 0.06;
    [JsonPropertyName("hud_course_align")] public string HudCourseAlign { get; set; } = "left";
    [JsonPropertyName("hud_course_scale")] public double HudCourseScale { get; set; } = 1.0;

    /// <summary>Kantenlaenge EINER Kachel in Bildpunkten, vor dem Massstab.</summary>
    [JsonPropertyName("course_shape_tile")] public int CourseShapeTile { get; set; } = 150;

    [JsonPropertyName("course_shape_line")] public string CourseShapeLine { get; set; } = "#7fd3ff";
    [JsonPropertyName("course_shape_start")] public string CourseShapeStart { get; set; } = "#ffd25a";
    [JsonPropertyName("course_shape_back")] public string CourseShapeBack { get; set; } = "#0d1117";
    [JsonPropertyName("course_shape_width")] public double CourseShapeWidth { get; set; } = 2.0;

    // ---- WIE DIE LINIE AUSSIEHT (seit 2026-09-25) ------------------------------
    //
    // "very pixely and low quality" (Nutzer): die Rivalen-Karten waren eine Wolke aus
    // Bildpunkten, die Telemetrie-Linie tausend Stueckchen von je fuenf Metern. Beides
    // laesst sich jetzt glaetten, und die Staerke hat jede Karte fuer sich.

    /// <summary>Glaettung der Kartenlinie der Anmeldung, 0 (roh) bis 100.</summary>
    [JsonPropertyName("course_shape_smooth")] public int CourseShapeSmooth { get; set; } = 50;

    /// <summary>
    /// Wie eine Rivalen-Karte erscheint: "line" (die Linie des Spiels, als glatter
    /// Linienzug nachgezeichnet) oder "image" (der Kartenausschnitt, wie ihn das
    /// Spiel zeigt).
    /// </summary>
    [JsonPropertyName("course_rivals_style")] public string CourseRivalsStyle { get; set; } = "line";

    /// <summary>
    /// Nach so vielen Sekunden verschwinden die Anmeldekarten von selbst; 0 heisst:
    /// erst beim Rennstart. Wie <see cref="ShowSeconds"/> beim Autovorschlag.
    /// </summary>
    /// <remarks>Seit 2026-09-26 -- vorher blieben sie bis zum Start stehen.</remarks>
    [JsonPropertyName("course_shape_seconds")] public double CourseShapeSeconds { get; set; } = 45;

    /// <summary>"horizontal" (nebeneinander) oder "vertical" (untereinander).</summary>
    [JsonPropertyName("course_shape_layout")] public string CourseShapeLayout { get; set; } = "horizontal";

    public bool RivalsAsImage =>
        string.Equals(CourseRivalsStyle?.Trim(), "image", StringComparison.OrdinalIgnoreCase);

    public bool CourseVertical =>
        string.Equals(CourseShapeLayout?.Trim(), "vertical", StringComparison.OrdinalIgnoreCase);

    /// <summary>Deckkraft der Unterlage, 0 = gar keine.</summary>
    [JsonPropertyName("course_shape_back_alpha")]
    public int CourseShapeBackAlpha { get; set; } = 150;

    // ---- Notiz zum gewaehlten Auto ------------------------------------ //
    //
    // Sie erscheint, solange die Telemetrie dieses Auto meldet -- in der Garage
    // und in der Autoauswahl also genau dann, wenn der Rahmen darauf steht.
    [JsonPropertyName("car_notes")] public bool CarNotes { get; set; } = true;

    /// <summary>
    /// In der Autonotiz auch das aufgespielte Tune: Name, Tuner, Beschreibung
    /// (seit 2026-09-26).
    /// </summary>
    [JsonPropertyName("car_note_tune")] public bool CarNoteTune { get; set; } = true;

    // ---- TUNE-SPEICHER (seit 2026-09-26) ---------------------------------------
    //
    // Das Spiel nimmt nur eine begrenzte Zahl heruntergeladener Tunes. Die Grenze
    // nennt es nirgends lesbar; am 2026-09-26 verweigerte es bei 995 den naechsten
    // Download. 1000 ist die Annahme -- einstellbar im Reiter "Tunes".

    /// <summary>Wie viele Tunes das Spiel hoechstens nimmt.</summary>
    [JsonPropertyName("tune_limit")] public int TuneLimit { get; set; } = 1000;

    /// <summary>Ab so wenigen freien Plaetzen wird gewarnt.</summary>
    [JsonPropertyName("tune_warn_free")] public int TuneWarnFree { get; set; } = 50;

    /// <summary>
    /// Telemetrie auch von anderen Geraeten im Netz annehmen (Standard: nur von
    /// diesem Rechner). Bis 2026-09-24 lauschte die App auf ALLEN Schnittstellen --
    /// jedes Geraet im WLAN haette ihr Telemetrie unterschieben koennen: Vibration,
    /// Overlay und aufgezeichnete Runden aus fremder Hand. Forza schickt an
    /// 127.0.0.1, so steht es auch in der Anleitung; wer von einem zweiten Rechner
    /// sendet, schaltet das hier bewusst ein.
    /// </summary>
    [JsonPropertyName("telemetry_from_lan")] public bool TelemetryFromLan { get; set; }

    /// <summary>
    /// Beste Runden an die Seite schicken -- aber nur, wenn sie die Bestenliste des
    /// Autos schlagen (LapAutoSubmit). AN, sofern nicht abgeschaltet: so gewollt,
    /// und der Hinweis beim ersten Start sagt es.
    /// </summary>
    [JsonPropertyName("submit_laps")] public bool SubmitLaps { get; set; } = true;

    /// <summary>Der Name, unter dem eingereichte Zeiten auf der Seite stehen.</summary>
    /// <remarks>Leer heisst: es wird nichts eingereicht -- ohne Namen keine Zeile.</remarks>
    [JsonPropertyName("gamertag")] public string? Gamertag { get; set; }

    [JsonPropertyName("hud_carnote_x")] public double HudCarNoteX { get; set; } = 0.02;
    [JsonPropertyName("hud_carnote_y")] public double HudCarNoteY { get; set; } = 0.62;
    [JsonPropertyName("hud_carnote_align")] public string HudCarNoteAlign { get; set; } = "left";
    [JsonPropertyName("hud_carnote_scale")] public double HudCarNoteScale { get; set; } = 1.0;

    /// <summary>Breite des Kastens in Bildpunkten, vor dem Massstab.</summary>
    [JsonPropertyName("carnote_width")] public int CarNoteWidth { get; set; } = 360;

    [JsonPropertyName("carnote_ink")] public string CarNoteInk { get; set; } = "#e7ecf3";
    [JsonPropertyName("carnote_title")] public string CarNoteTitle { get; set; } = "#7fd3ff";
    [JsonPropertyName("carnote_back")] public string CarNoteBack { get; set; } = "#0d1117";
    [JsonPropertyName("carnote_back_alpha")] public int CarNoteBackAlpha { get; set; } = 170;

    public HudPlacement CarNotePlacement => new(
        (float)HudCarNoteX, (float)HudCarNoteY, HudCarNoteAlign ?? "left",
        (float)HudCarNoteScale);

    // ---- DIE LIVE-KARTE IM RENNEN (seit 2026-09-25) --------------------------------
    //
    // EINE Strecke, waehrend gefahren wird: die Referenzrunde, die auch der Delta-
    // Streifen benutzt, der schon gefahrene Teil und das eigene Auto als Punkt.
    // Getrennt von den drei Umrissen der Anmeldung -- die gehoeren VOR das Rennen.
    // Aus, bis man sie anschaltet: ein neues Fenster ueber dem Rennen soll niemand
    // ungefragt bekommen (siehe "Overlay must not linger").

    [JsonPropertyName("hud_livemap")] public bool LiveMap { get; set; }
    [JsonPropertyName("hud_livemap_x")] public double HudLiveMapX { get; set; } = 0.985;
    [JsonPropertyName("hud_livemap_y")] public double HudLiveMapY { get; set; } = 0.30;
    [JsonPropertyName("hud_livemap_align")] public string HudLiveMapAlign { get; set; } = "right";
    [JsonPropertyName("hud_livemap_scale")] public double HudLiveMapScale { get; set; } = 1.0;

    /// <summary>Kantenlaenge der Karte in 1080p-Bildpunkten, vor dem Massstab.</summary>
    [JsonPropertyName("livemap_size")] public int LiveMapSize { get; set; } = 240;

    /// <summary>Der Punkt des eigenen Autos.</summary>
    [JsonPropertyName("livemap_car")] public string LiveMapCar { get; set; } = "#ffffff";

    /// <summary>Der schon gefahrene Teil der Runde.</summary>
    [JsonPropertyName("livemap_trail")] public string LiveMapTrail { get; set; } = "#ffd25a";

    /// <summary>Strichstaerke der Live-Karte in 1080p-Bildpunkten, vor dem Massstab.</summary>
    /// <remarks>Bis 2026-09-25 lieh sie sich die Staerke der Anmeldekarten.</remarks>
    [JsonPropertyName("livemap_width")] public double LiveMapWidth { get; set; } = 2.5;

    /// <summary>Glaettung der Live-Karte, 0 (roh) bis 100.</summary>
    [JsonPropertyName("livemap_smooth")] public int LiveMapSmooth { get; set; } = 50;

    public HudPlacement LiveMapPlacement => new(
        (float)HudLiveMapX, (float)HudLiveMapY, HudLiveMapAlign ?? "right",
        (float)HudLiveMapScale);

    public HudPlacement CoursePlacement => new(
        (float)HudCourseX, (float)HudCourseY, HudCourseAlign ?? "left",
        (float)HudCourseScale);

    public ShapeSource ShapeSourceChoice => (CourseShapeSource ?? "auto").Trim().ToLowerInvariant() switch
    {
        "telemetry" => ShapeSource.Telemetry,
        "rivals" => ShapeSource.Rivals,
        _ => ShapeSource.Auto,
    };

    // Farben als Hex, wie im Web. Ein vertippter Wert faellt auf die Vorgabe zurueck,
    // statt den Streifen schwarz zu lassen.
    [JsonPropertyName("hud_color_ahead")] public string HudColorAhead { get; set; } = "#7ee787";
    [JsonPropertyName("hud_color_behind")] public string HudColorBehind { get; set; } = "#ff6b6b";
    [JsonPropertyName("hud_color_neutral")] public string HudColorNeutral { get; set; } = "#e7ecf3";
    // Der Geister-Countdown als AMPEL: die Farbe traegt der Hintergrund, nicht die
    // Schrift. Im Augenwinkel ist eine Flaeche zu erkennen, eine Ziffernfarbe nicht.
    //
    //   ruhig   (bis auf die letzten Sekunden)  cyan
    //   Warnung (die letzten Sekunden davor)    gelb
    //   Einblendphase (nach dem Ende)           orange
    [JsonPropertyName("hud_color_ghost")] public string HudColorGhost { get; set; } = "#22b8e6";
    [JsonPropertyName("hud_color_ghost_warn")] public string HudColorGhostWarn { get; set; } = "#ffd21e";
    [JsonPropertyName("hud_color_popin")] public string HudColorPopIn { get; set; } = "#ff8c1a";

    /// <summary>Die Schrift auf der Ampelflaeche -- frei waehlbar, Vorgabe Schwarz.</summary>
    [JsonPropertyName("hud_color_ghost_text")] public string HudColorGhostText { get; set; } = "#000000";

    /// <summary>
    /// Wie viele Sekunden vor dem Ende des Geistes gewarnt wird.
    /// </summary>
    [JsonPropertyName("ghost_warn_seconds")] public double GhostWarnSeconds { get; set; } = 5;
    [JsonPropertyName("hud_color_label")] public string HudColorLabel { get; set; } = "#93a2b5";
    [JsonPropertyName("hud_background")] public string HudBackground { get; set; } = "#96080b10";

    // ------------------------------------------------------------------ //
    // Benannte Anordnungen
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Die Felder, die eine Anordnung ausmachen -- Lage, Groesse, Farbe.
    /// </summary>
    /// <remarks>
    /// Bewusst NICHT die ganze Einstellungsdatei: Tastenbelegung, Bildbereiche und
    /// Netzwerkangaben gehoeren nicht zu einer Anordnung. Wer "Rennen bei Nacht"
    /// laedt, will seine Anzeigen anders stehen haben, nicht seine Tasten getauscht.
    /// </remarks>
    private static readonly string[] LayoutKeys =
    {
        "hud_delta_x", "hud_delta_y", "hud_delta_align", "hud_delta_scale",
        "hud_ghost_x", "hud_ghost_y", "hud_ghost_align", "hud_ghost_scale",
        "hud_note_x", "hud_note_y", "hud_note_align", "hud_note_scale",
        "hud_inputs", "hud_inputs_x", "hud_inputs_y", "hud_inputs_align",
        "hud_inputs_scale", "hud_inputs_seconds", "hud_inputs_lookahead",
        "hud_inputs_line_width",
        "hud_color_now",
        "hud_color_mine", "hud_color_theirs",
        "hud_color_ahead", "hud_color_behind", "hud_color_neutral",
        "hud_color_ghost", "hud_color_ghost_warn", "hud_color_ghost_text",
        "hud_color_popin", "hud_color_label", "hud_background",
        // Seit 2026-09-25 auch Umriss und Autonotiz -- vorher fehlten beide, und eine
        // geladene Anordnung liess sie stehen, wo sie gerade waren.
        "hud_course_x", "hud_course_y", "hud_course_align", "hud_course_scale",
        "course_shape_tile", "course_shape_line", "course_shape_start",
        "course_shape_back", "course_shape_back_alpha", "course_shape_width",
        "hud_carnote_x", "hud_carnote_y", "hud_carnote_align", "hud_carnote_scale",
        "carnote_width", "carnote_ink", "carnote_title", "carnote_back",
        "carnote_back_alpha",
        "hud_livemap", "hud_livemap_x", "hud_livemap_y", "hud_livemap_align",
        "hud_livemap_scale", "livemap_size", "livemap_car", "livemap_trail",
        "course_shape_smooth", "course_rivals_style", "course_shape_layout",
        "livemap_width", "livemap_smooth",
    };

    /// <summary>Jede Einstellung nach ihrem Namen in der Datei.</summary>
    private static readonly Dictionary<string, System.Reflection.PropertyInfo> NachSchluessel =
        typeof(OverlaySettings).GetProperties()
            .Select(p => (p, n: p.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
                                 .OfType<JsonPropertyNameAttribute>().FirstOrDefault()?.Name))
            .Where(t => t.n is not null)
            .ToDictionary(t => t.n!, t => t.p);

    /// <summary>Fuer den Selbsttest: die Schluessel einer Anordnung, die es nicht gibt.</summary>
    internal static IEnumerable<string> UnknownLayoutKeys() =>
        LayoutKeys.Where(k => !NachSchluessel.ContainsKey(k));

    public static string LayoutFolder => System.IO.Path.Combine(AppInfo.DataFolder, "hud_layouts");

    /// <summary>Die Namen der abgelegten Anordnungen.</summary>
    public static IReadOnlyList<string> LayoutNames()
    {
        try
        {
            if (!Directory.Exists(LayoutFolder)) { return Array.Empty<string>(); }
            return Directory.GetFiles(LayoutFolder, "*.json")
                .Select(System.IO.Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static string LayoutPath(string name)
    {
        // Kein Pfad in einem Namen: "..\..\system" waere sonst eine gueltige
        // Anordnung, und die wuerde irgendwohin schreiben.
        var sauber = new string(name.Where(c =>
            !System.IO.Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (sauber.Length == 0) { sauber = "namenlos"; }
        return System.IO.Path.Combine(LayoutFolder, sauber + ".json");
    }

    /// <summary>Die aktuelle Anordnung unter einem Namen ablegen.</summary>
    public bool SaveLayout(string name)
    {
        try
        {
            Directory.CreateDirectory(LayoutFolder);
            var alle = JsonSerializer.SerializeToNode(this)?.AsObject();
            if (alle is null) { return false; }
            var nur = new System.Text.Json.Nodes.JsonObject();
            foreach (var key in LayoutKeys)
            {
                if (alle.TryGetPropertyValue(key, out var wert) && wert is not null)
                {
                    nur[key] = wert.DeepClone();
                }
            }
            File.WriteAllText(LayoutPath(name), nur.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Eine abgelegte Anordnung uebernehmen.</summary>
    public bool LoadLayout(string name)
    {
        try
        {
            var pfad = LayoutPath(name);
            if (!File.Exists(pfad)) { return false; }
            var gelesen = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonNode>(
                File.ReadAllText(pfad))?.AsObject();
            if (gelesen is null) { return false; }

            // Ueber den eigenen Zustand legen und neu einlesen: so gilt fuer jedes
            // Feld dieselbe Umwandlung wie beim Laden der Datei, und ein fehlendes
            // Feld behaelt einfach seinen Wert.
            var jetzt = JsonSerializer.SerializeToNode(this)?.AsObject();
            if (jetzt is null) { return false; }
            foreach (var (key, wert) in gelesen)
            {
                if (wert is not null) { jetzt[key] = wert.DeepClone(); }
            }
            var neu = jetzt.Deserialize<OverlaySettings>();
            if (neu is null) { return false; }
            foreach (var key in LayoutKeys) { CopyLayoutField(neu, key); }
            Save();
            return true;
        }
        catch (Exception) { return false; }
    }

    public bool DeleteLayout(string name)
    {
        try
        {
            var pfad = LayoutPath(name);
            if (!File.Exists(pfad)) { return false; }
            File.Delete(pfad);
            return true;
        }
        catch (Exception) { return false; }
    }

    private void CopyLayoutField(OverlaySettings von, string key)
    {
        // NACH DEM NAMEN IN DER DATEI, nicht ueber eine zweite Liste von Faellen:
        // die stand bis zum 2026-09-25 neben LayoutKeys und musste von Hand
        // mitgezogen werden. Was dort fehlte, wurde still nicht kopiert.
        if (NachSchluessel.TryGetValue(key, out var eigenschaft) && eigenschaft.CanWrite)
        {
            eigenschaft.SetValue(this, eigenschaft.GetValue(von));
        }
    }

    /// <summary>
    /// Eine Farbe aus der Einstellung lesen -- ein Tippfehler faellt auf die Vorgabe.
    /// </summary>
    /// <remarks>
    /// An EINER Stelle, weil sonst der Streifen, die Vorschau und der Farbwaehler
    /// je eigene Vorstellungen davon haetten, was "#96080b10" bedeutet.
    /// </remarks>
    public static System.Drawing.Color ParseColour(string? value, System.Drawing.Color fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : System.Drawing.ColorTranslator.FromHtml(value);
        }
        catch (Exception) { return fallback; }
    }

    /// <summary>Und zurueck -- mit Deckkraft, wo sie nicht voll ist.</summary>
    public static string ToHex(System.Drawing.Color c) =>
        c.A == 255 ? $"#{c.R:x2}{c.G:x2}{c.B:x2}"
                   : $"#{c.A:x2}{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>
    /// Lage, Anker und Groesse EINES Stuecks im Reiter "Lap delta HUD" -- fuer alle
    /// sechs an einer Stelle, jedes ausdruecklich.
    /// </summary>
    /// <remarks>
    /// Bis zum 2026-09-25 standen diese Weichen dreimal im Reiter, jede mit
    /// "default: Delta" -- und kannten Umriss und Autonotiz nicht. Groessenregler,
    /// Mausrad und Anker veraenderten fuer diese beiden also die DELTA-ZAHL, waehrend
    /// sie selbst unveraendert stehen blieben. Ein neues Stueck, das hier fehlt,
    /// wirft jetzt, statt still ein anderes zu verstellen.
    /// </remarks>
    public HudPlacement PlacementOf(HudPart part) => part switch
    {
        HudPart.Delta => DeltaPlacement,
        HudPart.Ghost => GhostPlacement,
        HudPart.Note => NotePlacement,
        HudPart.Inputs => InputsPlacement,
        HudPart.Course => CoursePlacement,
        HudPart.CarNote => CarNotePlacement,
        HudPart.LiveMap => LiveMapPlacement,
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, "unbekanntes HUD-Stueck"),
    };

    public void SetScale(HudPart part, double wert)
    {
        switch (part)
        {
            case HudPart.Delta: HudDeltaScale = wert; break;
            case HudPart.Ghost: HudGhostScale = wert; break;
            case HudPart.Note: HudNoteScale = wert; break;
            case HudPart.Inputs: HudInputsScale = wert; break;
            case HudPart.Course: HudCourseScale = wert; break;
            case HudPart.CarNote: HudCarNoteScale = wert; break;
            case HudPart.LiveMap: HudLiveMapScale = wert; break;
            default: throw new ArgumentOutOfRangeException(nameof(part), part, "unbekanntes HUD-Stueck");
        }
    }

    public void SetAlign(HudPart part, string wert)
    {
        switch (part)
        {
            case HudPart.Delta: HudDeltaAlign = wert; break;
            case HudPart.Ghost: HudGhostAlign = wert; break;
            case HudPart.Note: HudNoteAlign = wert; break;
            case HudPart.Inputs: HudInputsAlign = wert; break;
            case HudPart.Course: HudCourseAlign = wert; break;
            case HudPart.CarNote: HudCarNoteAlign = wert; break;
            case HudPart.LiveMap: HudLiveMapAlign = wert; break;
            default: throw new ArgumentOutOfRangeException(nameof(part), part, "unbekanntes HUD-Stueck");
        }
    }

    public void SetPosition(HudPart part, double x, double y)
    {
        switch (part)
        {
            case HudPart.Delta: HudDeltaX = x; HudDeltaY = y; break;
            case HudPart.Ghost: HudGhostX = x; HudGhostY = y; break;
            case HudPart.Note: HudNoteX = x; HudNoteY = y; break;
            case HudPart.Inputs: HudInputsX = x; HudInputsY = y; break;
            case HudPart.Course: HudCourseX = x; HudCourseY = y; break;
            case HudPart.CarNote: HudCarNoteX = x; HudCarNoteY = y; break;
            case HudPart.LiveMap: HudLiveMapX = x; HudLiveMapY = y; break;
            default: throw new ArgumentOutOfRangeException(nameof(part), part, "unbekanntes HUD-Stueck");
        }
    }

    [JsonIgnore]
    public HudPlacement DeltaPlacement => new(
        (float)HudDeltaX, (float)HudDeltaY, HudDeltaAlign ?? "center", (float)HudDeltaScale);

    [JsonIgnore]
    public HudPlacement GhostPlacement => new(
        (float)HudGhostX, (float)HudGhostY, HudGhostAlign ?? "left", (float)HudGhostScale);

    [JsonIgnore]
    public HudPlacement NotePlacement => new(
        (float)HudNoteX, (float)HudNoteY, HudNoteAlign ?? "left", (float)HudNoteScale);

    /// <summary>
    /// Wie lange die Kollisionsfreiheit im Mehrspielerstart gilt, ab "GO".
    /// </summary>
    /// <remarks>
    /// Steht in KEINEM Telemetriefeld -- gezaehlt wird ab dem Rennstart, den
    /// `CurrentRaceTime` markiert. 26 Sekunden sind die vom Spieler gemessene
    /// Dauer (2026-09-12); wer eine andere beobachtet, aendert sie hier.
    /// </remarks>
    /// <summary>
    /// Wie lange nach dem "GO" niemand jemanden anfassen kann.
    /// </summary>
    /// <remarks>
    /// Steht in keinem Telemetriefeld -- gezaehlt wird ab dem Rennstart. 25 s vom
    /// Nutzer gemessen (2026-09-12).
    /// </remarks>
    [JsonPropertyName("ghost_seconds")] public double GhostSeconds { get; set; } = 25;

    /// <summary>
    /// Die Sekunden DANACH, in denen die Fahrer wieder auftauchen.
    /// </summary>
    /// <remarks>
    /// Das Ende des Geistes ist nicht der Moment, in dem es sicher ist: danach
    /// ploppen die anderen nacheinander wieder ins Feld, und genau dann passieren
    /// die Unfaelle. Diese Spanne wird darum eigens hochgezaehlt, in Rot.
    /// </remarks>
    [JsonPropertyName("popin_seconds")] public double PopInSeconds { get; set; } = 5;

    /// <summary>
    /// Ein frei waehlbares Wort, das jede aufgezeichnete Runde mitbekommt.
    /// </summary>
    /// <remarks>
    /// Es wird zum Ordner im Rundenarchiv. Damit lassen sich Sitzungen trennen, die
    /// sich sonst nicht unterscheiden liessen -- "regen", "abstimmung-b", "abend".
    /// Die Telemetrie kennt so etwas nicht; nur der Fahrer weiss, warum diese Runden
    /// zusammengehoeren.
    /// </remarks>
    [JsonPropertyName("lap_tag")] public string LapTag { get; set; } = string.Empty;

    /// <summary>In welchem Modus gerade gefahren wird -- oder "auto".</summary>
    /// <remarks>
    /// DIE TELEMETRIE SAGT DEN MODUS NICHT, und der Anmeldeschirm nennt die
    /// Rennart (Road Racing, Street Racing), nicht den Modus. Sicher erschliessen
    /// laesst sich allein die freie Welt: dort stoppt die eigene Uhr.
    ///
    /// Wer weiss, was er gerade spielt, stellt es hier ein, und jede Runde traegt
    /// es mit -- mit `modeEvidence = "setting"`, damit spaeter unterscheidbar
    /// bleibt, was gemessen und was angegeben wurde. "auto" laesst es bei dem, was
    /// die App selbst feststellen kann.
    /// </remarks>
    [JsonPropertyName("lap_mode")] public string LapMode { get; set; } = "auto";

    /// <summary>Die volle Telemetrie je Runde in eine eigene Datei schreiben.</summary>
    /// <remarks>
    /// Kostet rund 1,5 MB je Runde (gemessen: 187 s bei 60 Hz, 72 Spalten, gepackt).
    /// Die Rundendatei selbst bleibt klein und unberuehrt; die volle Spur liegt als
    /// `.tele.gz` daneben und wird nur von dem gelesen, der nachsimulieren will.
    /// Wer den Platz nicht ausgeben will, schaltet es hier ab.
    /// </remarks>
    [JsonPropertyName("full_telemetry")] public bool FullTelemetry { get; set; } = true;

    /// <summary>Ob jede Runde zusaetzlich ins Archiv geschrieben wird.</summary>
    [JsonPropertyName("archive_laps")] public bool ArchiveLaps { get; set; } = true;

    [JsonPropertyName("gamepad_right")] public List<string> GamepadRight { get; set; } = new() { "BACK" };
    [JsonPropertyName("gamepad_left")] public List<string> GamepadLeft { get; set; } = new() { "LEFT_THUMB" };

    [JsonPropertyName("side_width_fraction")] public double SideWidthFraction { get; set; } = 0.26;
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 0.9;

    /// <summary>Der Ausschnitt mit der Streckenliste, in Bruchteilen des Bildes.</summary>
    /// <remarks>
    /// LINKE KANTE AM 2026-09-16 VON 0.16 AUF 0.12 GERUECKT.
    ///
    /// Unter jedem Streckennamen steht seine Laenge ("8.5 KM - 3 LAPS"), und die
    /// wird jetzt gelesen -- sie ist die einzige Bruecke zwischen dem Namen auf dem
    /// Schirm und dem eigenen Kursordner, der nur Koordinaten kennt. Auf dem
    /// Bildschirmfoto vom 2026-09-16 (3168 Punkte breit) beginnt diese Zeile bei
    /// Bruchteil 0.139. Bei 0.16 schnitt die Maske ihre ersten Zeichen ab.
    ///
    /// Beim Namen war das folgenlos, der wird unscharf verglichen und uebersteht
    /// ein fehlendes "Su". Bei einer ZAHL ist es das Gegenteil von folgenlos: aus
    /// "8.5 KM" wird "5 KM", und das ist keine unlesbare Zahl, sondern eine falsche
    /// -- sie wuerde eine falsche Strecke ueberzeugend bestaetigen.
    ///
    /// Nach links ist Platz: die Nummernspalte "01 / 02 / 03" sitzt bei 0.03.
    /// </remarks>
    [JsonPropertyName("region_routes")] public double[]? RegionRoutes { get; set; }
        = { 0.12, 0.165, 0.62, 0.62 };
    [JsonPropertyName("region_class")] public double[]? RegionClass { get; set; }
        = { 0.76, 0.15, 0.90, 0.28 };

    [JsonPropertyName("min_tracks")] public int MinTracks { get; set; } = 2;
    [JsonPropertyName("max_tracks")] public int MaxTracks { get; set; } = 3;
    [JsonPropertyName("track_cutoff")] public double TrackCutoff { get; set; } = 0.62;
    [JsonPropertyName("change_threshold")] public double ChangeThreshold { get; set; } = 2.0;

    [JsonPropertyName("categories_expected")] public List<string> CategoriesExpected { get; set; }
        = new() { "Road Racing", "Street Racing", "Touge", "Dirt Racing" };

    /// <summary>Where to ask for the newest dataset. Empty means "never ask".</summary>
    /// <remarks>
    /// The scanning machine serves this; every other machine reads from it. Set it to
    /// an empty string to work purely from the local file -- the app then never opens
    /// a socket of its own.
    /// </remarks>
    [JsonPropertyName("dataset_url")] public string DatasetUrl { get; set; }
        = DefaultServer;

    /// <summary>Der Server, seit 2026-09-25 ueber HTTPS (siehe ServerHttp).</summary>
    /// <remarks>
    /// Steht nicht im Quelltext: er kommt beim Bauen aus config/local.json (siehe
    /// AppInfo.DefaultServer). Ohne diese Datei ist er leer.
    /// </remarks>
    public static string DefaultServer => AppInfo.DefaultServer;

    /// <summary>Die alte Vorgabe: derselbe Rechner ueber http. Wer sie noch in seiner
    /// Datei stehen hat, bekommt beim Laden die neue -- eine selbst eingetragene
    /// Adresse bleibt, wie sie ist.</summary>
    internal static string? OldDefaultServer =>
        DefaultServer.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "http://" + DefaultServer["https://".Length..] : null;

    internal static string? UpgradeServer(string? url) =>
        url is not null && OldDefaultServer is { } alt
        && string.Equals(url.Trim().TrimEnd('/'), alt, StringComparison.OrdinalIgnoreCase)
            ? DefaultServer : url;

    /// <summary>Seconds to wait for the small summary before giving up on the server.</summary>
    /// <remarks>
    /// Short on purpose: this runs at startup, and a server that is off must not make
    /// the app look hung. The download that may follow gets its own, longer budget.
    /// </remarks>
    [JsonPropertyName("dataset_probe_seconds")] public double DatasetProbeSeconds { get; set; } = 4;

    [JsonPropertyName("dataset_download_seconds")] public double DatasetDownloadSeconds { get; set; } = 120;

    /// <summary>Die Fassung der App, nach der beim Start nicht mehr gefragt wird.</summary>
    /// <remarks>
    /// Wer beim Update "ueberspringen" waehlt, meint diese eine Fassung -- nicht
    /// "nie wieder fragen". Darum steht hier eine Kennung und kein Schalter: die
    /// naechste Fassung hat eine andere, und die Frage kommt wieder. Ein
    /// Ja-Nein-Schalter waere nach dem ersten Ueberspringen fuer immer aus, und
    /// genau das faellt erst auf, wenn ein wichtiger Fehler behoben wurde und
    /// niemand die Korrektur bekommt.
    /// </remarks>
    [JsonPropertyName("skipped_update")] public string? SkippedUpdate { get; set; }

    /// <summary>Only let the haptics touch the controller while Forza runs.</summary>
    /// <remarks>
    /// The 50 Hz firewall exists to overwrite the GAME's rumble. Outside the game it
    /// would be overwriting whatever else the controller is doing -- another game,
    /// Steam's own tests -- which nobody asked for. Default on; set it to false to
    /// drive an actuator from a constant node with the game closed.
    ///
    /// The vibration-test buttons and the tone test are never gated: they are an
    /// explicit press by someone sitting in front of the app.
    /// </remarks>
    [JsonPropertyName("haptics_require_forza")]
    public bool HapticsRequireForza { get; set; } = true;

    /// <summary>Also require telemetry to be arriving before driving the controller.</summary>
    /// <remarks>
    /// This is not the same guard as the one above, and it catches the more damaging
    /// case. With Forza running but Data Out off, the graph has no input, so the
    /// firewall would assert zeros at 50 Hz -- overwriting the game's OWN rumble with
    /// silence. From the outside that looks like the app broke the controller.
    ///
    /// It also releases the controller in menus and on a pause, where the game stops
    /// sending: the game's own rumble then works as it always did.
    /// </remarks>
    [JsonPropertyName("haptics_require_telemetry")]
    public bool HapticsRequireTelemetry { get; set; } = true;

    /// <summary>Only let the overlay panels appear while Forza runs.</summary>
    /// <remarks>
    /// A panel over the desktop is a bug, not a feature: it reads the game's screen
    /// and answers about the race that is being offered. With the game gone, a
    /// hotkey does nothing and an open panel is taken down.
    /// </remarks>
    [JsonPropertyName("overlay_require_forza")]
    public bool OverlayRequireForza { get; set; } = true;

    /// <summary>Only show the panels while Forza is the ACTIVE window.</summary>
    /// <remarks>
    /// Running was not enough. Until 2026-09-15 only the process was checked, so
    /// alt-tabbing to the desktop left the panels standing over it -- and because
    /// they never take a click, they could not be clicked away either.
    ///
    /// Set this to false to see the panels beside the game while setting them up;
    /// then a running process is enough again.
    /// </remarks>
    [JsonPropertyName("overlay_require_focus")]
    public bool OverlayRequireFocus { get; set; } = true;

    /// <summary>Process to look for, without ".exe". Empty means the default.</summary>
    [JsonPropertyName("forza_process")] public string ForzaProcess { get; set; }
        = "forzahorizon6";

    /// <summary>Watch the local dataset file and reload when it is rewritten.</summary>
    /// <remarks>
    /// Only useful on the scanning machine, where a sweep rewrites it every five
    /// boards -- but harmless elsewhere, since the watcher simply never fires.
    /// </remarks>
    [JsonPropertyName("dataset_watch_local")] public bool DatasetWatchLocal { get; set; } = true;

    /// <summary>Die Sprache der Oberflaeche. "auto" heisst: die von Windows.</summary>
    /// <remarks>
    /// Siehe <see cref="Loc"/>. Vorgabe ist "auto": wer Windows auf Deutsch
    /// benutzt, soll die App nicht erst umstellen muessen -- und wer sie lieber
    /// englisch haette, findet die Auswahl oben im Fenster.
    /// </remarks>
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";

    /// <summary>Gar nicht mit dem Server reden.</summary>
    /// <remarks>
    /// Die Erklaerung beim ersten Start sagt zu, dass sich die Abfragen abschalten
    /// lassen. Diese Zusage hat vorher NICHT gestimmt -- es gab keinen Schalter.
    /// Jetzt gibt es einen: steht er auf true, bekommen DatasetSync und AppUpdate
    /// eine leere Adresse, und beide behandeln das seit je als "kein Server
    /// eingestellt". Die App laeuft dann vollstaendig aus dem mitgelieferten
    /// Datenbestand.
    ///
    /// Nichts zu versprechen, was man nicht hat, ist der eine Teil; das
    /// Versprochene dann auch zu bauen, der andere.
    /// </remarks>
    [JsonPropertyName("offline")] public bool Offline { get; set; }

    /// <summary>Die Serveradresse -- oder nichts, wenn offline gewuenscht ist.</summary>
    [JsonIgnore]
    public string ServerUrl => Offline ? string.Empty : (DatasetUrl ?? string.Empty);

    /// <summary>Welcher Fassung der Erklaerung zugestimmt wurde. 0 = noch keiner.</summary>
    /// <remarks>
    /// Siehe <see cref="Disclosure"/>. Die Zahl und nicht ein blosses "ja", damit
    /// eine Aenderung an dem, WAS die App tut, erneut gefragt werden kann. Wer die
    /// Zahl von Hand hochsetzt, ueberspringt die Frage -- das ist seine Sache, der
    /// Text liegt daneben.
    /// </remarks>
    [JsonPropertyName("disclosure_ack")] public int DisclosureAcknowledged { get; set; }

    [JsonIgnore] public string? Path { get; private set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string? FindPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "config", "overlay.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    public static OverlaySettings Load(string? path = null)
    {
        path ??= FindPath();
        if (path is null || !File.Exists(path))
        {
            return new OverlaySettings { Path = path };
        }
        try
        {
            var text = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<OverlaySettings>(text, Options)
                         ?? new OverlaySettings();
            loaded.Path = path;
            // Jede ausgelieferte Datei nennt noch http:// -- ohne diese Zeile bliebe
            // jede bestehende Installation auf dem unverschluesselten Weg.
            loaded.DatasetUrl = UpgradeServer(loaded.DatasetUrl)!;
            return loaded;
        }
        catch (Exception)
        {
            // A settings file nobody can parse must not stop the app: defaults, and
            // the tab shows where the file is so it can be fixed or deleted.
            return new OverlaySettings { Path = path };
        }
    }

    public bool Save()
    {
        if (Path is null)
        {
            return false;
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool ShowsPoints => !string.Equals(ScoreMode, "time", StringComparison.OrdinalIgnoreCase);
}
