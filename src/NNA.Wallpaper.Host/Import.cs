using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NNA.Wallpaper.Host.Config;
using NNA.Wallpaper.Host.Services;

namespace NNA.Wallpaper.Host;

/// <summary>
/// Imports configuration from an old-style wallpaper folder (the shape of
/// <c>H:\brand\wallpaper</c>: <c>helper\launch.json</c>, <c>helper\events.json</c>,
/// <c>helper\icons\*.png</c>, <c>helper\config.json</c> (open_with only; the helper's auth token is
/// never imported), <c>helper\helper.py</c> (GRAPHS / WEATHER constants, read with regexes — it is
/// not executed), <c>block\pin</c> (photo folder) and <c>shared\nna-config.js</c> (a hand-tolerant
/// parse of the <c>window.NNA_CONFIG</c> object literal). All writes go through
/// <see cref="ConfigStore"/>, so they are atomic.
/// </summary>
public static class Import
{
    /// <summary>
    /// Runs the import. Throws <see cref="DirectoryNotFoundException"/> (and touches nothing) when
    /// <paramref name="sourceDir"/> itself does not exist; every other missing piece is optional and
    /// only adds a warning to the report.
    /// </summary>
    public static ImportReport Run(Paths paths, ConfigStore cfg, Log log, string sourceDir, IReadOnlyList<MonitorStatus> monitors)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException("import source not found: " + sourceDir);

        var report = new ImportReport();
        var helperDir = Path.Combine(sourceDir, "helper");

        ImportLaunch(cfg, helperDir, report);
        ImportEvents(cfg, helperDir, report);
        ImportIcons(paths, helperDir, report);
        var openWith = ImportOpenWith(helperDir, report);

        var nnaRoot = ImportNnaConfig(sourceDir, report);
        ImportPhotosWidget(cfg, sourceDir, nnaRoot, report);
        ImportWidgetSettings(cfg, nnaRoot, report);

        var app = cfg.App;
        if (openWith.Count > 0)
        {
            app.OpenWith = openWith;
            app.OpenRoots = openWith.Keys.Select(k => k.EndsWith('\\') ? k : k + "\\").ToList();
        }
        ImportHelperPy(app, helperDir, report);
        cfg.SaveApp();

        EnsureMonitors(cfg, monitors);

        log.Info("import complete: " + report);
        return report;
    }

    private static void ImportLaunch(ConfigStore cfg, string helperDir, ImportReport report)
    {
        var file = Path.Combine(helperDir, "launch.json");
        if (!File.Exists(file)) { report.Warnings.Add("launch.json not found: " + file); return; }
        if (Json.LoadFile(file) is not JsonObject obj) { report.Warnings.Add("launch.json: could not parse"); return; }
        cfg.SaveLaunch(obj);
        report.LaunchGroups = (obj["groups"] as JsonArray)?.Count ?? 0;
        report.LaunchItems = (obj["items"] as JsonObject)?.Count ?? 0;
    }

    private static void ImportEvents(ConfigStore cfg, string helperDir, ImportReport report)
    {
        var file = Path.Combine(helperDir, "events.json");
        if (!File.Exists(file)) { report.Warnings.Add("events.json not found: " + file); return; }
        if (Json.LoadFile(file) is not JsonObject obj) { report.Warnings.Add("events.json: could not parse"); return; }
        cfg.SaveEvents(obj);
        report.Events = (obj["events"] as JsonArray)?.Count ?? 0;
        report.Daily = (obj["daily"] as JsonArray)?.Count ?? 0;
    }

    private static void ImportIcons(Paths paths, string helperDir, ImportReport report)
    {
        var iconsSrc = Path.Combine(helperDir, "icons");
        if (!Directory.Exists(iconsSrc)) { report.Warnings.Add("icons folder not found: " + iconsSrc); return; }
        Directory.CreateDirectory(paths.IconsDir);
        foreach (var file in Directory.GetFiles(iconsSrc, "*.png"))
        {
            File.Copy(file, Path.Combine(paths.IconsDir, Path.GetFileName(file)), overwrite: true);
            report.Icons++;
        }
    }

    /// <summary>Only the open_with map is imported from helper\config.json; the helper token is never read.</summary>
    private static Dictionary<string, string[]> ImportOpenWith(string helperDir, ImportReport report)
    {
        var result = new Dictionary<string, string[]>();
        var file = Path.Combine(helperDir, "config.json");
        if (!File.Exists(file)) { report.Warnings.Add("config.json not found: " + file); return result; }
        try
        {
            if (Json.LoadFile(file) is JsonObject root && root["open_with"] is JsonObject ow)
            {
                foreach (var kv in ow)
                {
                    if (kv.Value is JsonArray arr)
                        result[kv.Key] = arr.Select(v => v?.GetValue<string>() ?? "").ToArray();
                }
            }
            else
            {
                report.Warnings.Add("config.json: no open_with map");
            }
        }
        catch (Exception ex)
        {
            report.Warnings.Add("config.json: " + ex.Message);
        }
        return result;
    }

    private static void ImportPhotosWidget(ConfigStore cfg, string sourceDir, JsonObject? nnaRoot, ImportReport report)
    {
        var pinDir = Path.Combine(sourceDir, "block", "pin");
        if (!Directory.Exists(pinDir)) { report.Warnings.Add("pin folder not found: " + pinDir); return; }

        var pins = nnaRoot?["pins"] as JsonObject;
        var photos = new JsonObject
        {
            ["folder"] = Path.GetFullPath(pinDir),
            ["intervalMin"] = pins?["intervalMin"]?.DeepClone() ?? JsonValue.Create(10),
            ["fadeMs"] = pins?["fadeMs"]?.DeepClone() ?? JsonValue.Create(1400),
            ["zoom"] = pins?["zoom"]?.DeepClone() ?? JsonValue.Create(true),
            ["zoomScale"] = pins?["zoomScale"]?.DeepClone() ?? JsonValue.Create(1.08),
            ["order"] = pins?["order"]?.DeepClone() ?? JsonValue.Create("sequential"),
        };
        cfg.SaveWidgetSettings("photos", photos);
        report.WidgetsWritten++;
    }

    private static void ImportWidgetSettings(ConfigStore cfg, JsonObject? nnaRoot, ImportReport report)
    {
        if (nnaRoot is null) return;

        if (nnaRoot["eq"] is JsonObject eq)
        {
            cfg.SaveWidgetSettings("eq", (JsonObject)eq.DeepClone());
            report.WidgetsWritten++;
        }
        if (nnaRoot["focus"] is JsonObject focus)
        {
            cfg.SaveWidgetSettings("focus", (JsonObject)focus.DeepClone());
            report.WidgetsWritten++;
        }
        if (nnaRoot["weather"] is JsonObject weather)
        {
            var w = (JsonObject)weather.DeepClone();
            if (nnaRoot["clocks"] is JsonArray clocks) w["clocks"] = clocks.DeepClone();
            cfg.SaveWidgetSettings("weather", w);
            report.WidgetsWritten++;
        }
        if (nnaRoot["launch"] is JsonObject launchWidget)
        {
            cfg.SaveWidgetSettings("launch", (JsonObject)launchWidget.DeepClone());
            report.WidgetsWritten++;
        }
        if (nnaRoot["events"] is JsonObject eventsWidget)
        {
            cfg.SaveWidgetSettings("events", (JsonObject)eventsWidget.DeepClone());
            report.WidgetsWritten++;
        }
        if (nnaRoot["graph"] is JsonObject graphWidget)
        {
            cfg.SaveWidgetSettings("graph", (JsonObject)graphWidget.DeepClone());
            report.WidgetsWritten++;
        }
    }

    private static void ImportHelperPy(AppSettings app, string helperDir, ImportReport report)
    {
        var file = Path.Combine(helperDir, "helper.py");
        if (!File.Exists(file)) { report.Warnings.Add("helper.py not found: " + file); return; }
        var text = File.ReadAllText(file);

        var graphs = ParseGraphs(text, report.Warnings);
        if (graphs.Count > 0) app.Graphs = graphs;

        var weather = ParseWeather(text, report.Warnings);
        if (weather is not null) app.Weather = weather;
    }

    /// <summary>Reads GRAPHS = { 'h': r'...', 'e': r'...' } out of helper.py without executing it.</summary>
    private static Dictionary<string, string> ParseGraphs(string helperPyText, List<string> warnings)
    {
        var result = new Dictionary<string, string>();
        var block = Regex.Match(helperPyText, @"GRAPHS\s*=\s*\{(?<body>[^}]*)\}");
        if (!block.Success) { warnings.Add("helper.py: GRAPHS dict not found"); return result; }

        foreach (Match m in Regex.Matches(block.Groups["body"].Value, @"['""](?<key>[^'""]+)['""]\s*:\s*r?['""](?<val>[^'""]*)['""]"))
        {
            result[m.Groups["key"].Value] = m.Groups["val"].Value;
        }
        if (result.Count == 0) warnings.Add("helper.py: GRAPHS dict was empty after parsing");
        return result;
    }

    /// <summary>Reads WEATHER = {'lat': .., 'lon': .., 'tz': '..', 'name': '..'} out of helper.py.</summary>
    private static WeatherSettings? ParseWeather(string helperPyText, List<string> warnings)
    {
        var block = Regex.Match(helperPyText, @"WEATHER\s*=\s*\{(?<body>[^}]*)\}");
        if (!block.Success) { warnings.Add("helper.py: WEATHER dict not found"); return null; }
        var body = block.Groups["body"].Value;

        var lat = Regex.Match(body, @"['""]lat['""]\s*:\s*(?<v>-?[\d.]+)");
        var lon = Regex.Match(body, @"['""]lon['""]\s*:\s*(?<v>-?[\d.]+)");
        if (!lat.Success || !lon.Success) { warnings.Add("helper.py: WEATHER dict missing lat/lon"); return null; }

        var tz = Regex.Match(body, @"['""]tz['""]\s*:\s*['""](?<v>[^'""]*)['""]");
        var name = Regex.Match(body, @"['""]name['""]\s*:\s*['""](?<v>[^'""]*)['""]");
        var defaults = new WeatherSettings();
        return new WeatherSettings
        {
            Lat = double.Parse(lat.Groups["v"].Value, CultureInfo.InvariantCulture),
            Lon = double.Parse(lon.Groups["v"].Value, CultureInfo.InvariantCulture),
            Tz = tz.Success ? tz.Groups["v"].Value : defaults.Tz,
            Name = name.Success ? name.Groups["v"].Value : defaults.Name,
        };
    }

    /// <summary>
    /// If nothing has been laid out yet (fresh install), seed monitors.json from the default
    /// landscape/portrait layouts, named after the real monitors when the engine reported any.
    /// An existing, non-empty layout is left untouched.
    /// </summary>
    private static void EnsureMonitors(ConfigStore cfg, IReadOnlyList<MonitorStatus> monitors)
    {
        if (cfg.Monitors.Monitors.Count > 0) return;

        var next = new MonitorsConfig();
        if (monitors.Count == 0)
        {
            next.Monitors.Add(LayoutResolver.DefaultMain("main"));
            next.Monitors.Add(LayoutResolver.DefaultVertical("vertical"));
        }
        else
        {
            foreach (var m in monitors)
            {
                var layout = m.height > m.width ? LayoutResolver.DefaultVertical(m.id) : LayoutResolver.DefaultMain(m.id);
                if (!string.IsNullOrEmpty(m.name)) layout.Name = m.name;
                next.Monitors.Add(layout);
            }
        }
        cfg.ReplaceMonitors(next);
    }

    /// <summary>
    /// Tolerant parse of the <c>window.NNA_CONFIG = { ... };</c> object literal in nna-config.js:
    /// strips <c>//</c> comments, drops trailing commas, turns single-quoted strings and bare
    /// identifier keys into valid JSON, then parses. On any failure this returns null and the
    /// caller falls back to defaults for everything except <c>dim</c> (recovered separately below).
    /// </summary>
    private static JsonObject? ImportNnaConfig(string sourceDir, ImportReport report)
    {
        var file = Path.Combine(sourceDir, "shared", "nna-config.js");
        if (!File.Exists(file)) { report.Warnings.Add("nna-config.js not found: " + file); return null; }

        var text = File.ReadAllText(file);
        var (obj, warnings) = ParseNnaConfig(text);
        report.Warnings.AddRange(warnings);
        if (obj is not null) return obj;

        // Best-effort fallback: recover just "dim" with a plain regex so app.Theme.Dim still imports.
        var dimMatch = Regex.Match(text, @"dim\s*:\s*(?<v>[\d.]+)");
        if (dimMatch.Success)
        {
            report.Warnings.Add("nna-config.js: full parse failed, recovered dim only");
            return new JsonObject { ["dim"] = double.Parse(dimMatch.Groups["v"].Value, CultureInfo.InvariantCulture) };
        }
        report.Warnings.Add("nna-config.js: parse failed, no settings recovered (defaults kept)");
        return null;
    }

    private static (JsonObject? obj, List<string> warnings) ParseNnaConfig(string text)
    {
        var warnings = new List<string>();
        try
        {
            const string marker = "NNA_CONFIG";
            var markerIdx = text.IndexOf(marker, StringComparison.Ordinal);
            if (markerIdx < 0) { warnings.Add("nna-config.js: NNA_CONFIG marker not found"); return (null, warnings); }

            var braceStart = text.IndexOf('{', markerIdx);
            if (braceStart < 0) { warnings.Add("nna-config.js: no opening brace after NNA_CONFIG"); return (null, warnings); }

            var end = FindMatchingBrace(text, braceStart);
            if (end < 0) { warnings.Add("nna-config.js: unbalanced braces"); return (null, warnings); }

            var raw = text.Substring(braceStart, end - braceStart + 1);
            raw = StripLineComments(raw);
            raw = Regex.Replace(raw, @",(\s*[}\]])", "$1");
            raw = Regex.Replace(raw, @"'((?:[^'\\]|\\.)*)'", m => "\"" + m.Groups[1].Value.Replace("\"", "\\\"") + "\"");
            raw = Regex.Replace(raw, @"([{,]\s*)([A-Za-z_$][A-Za-z0-9_$]*)\s*:", "$1\"$2\":");

            var node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (node is not JsonObject obj) { warnings.Add("nna-config.js: parsed value was not an object"); return (null, warnings); }
            return (obj, warnings);
        }
        catch (Exception ex)
        {
            warnings.Add("nna-config.js: parse failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            return (null, warnings);
        }
    }

    /// <summary>Finds the index of the '}' that matches the '{' at <paramref name="openIndex"/>, skipping quoted strings and // comments.</summary>
    private static int FindMatchingBrace(string text, int openIndex)
    {
        var depth = 0;
        var inLineComment = false;
        var inString = false;
        var stringChar = '\0';
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
                continue;
            }
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == stringChar) inString = false;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { inLineComment = true; continue; }
            if (c == '\'' || c == '"') { inString = true; stringChar = c; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static string StripLineComments(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var inString = false;
        var stringChar = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
                if (c == stringChar) inString = false;
                continue;
            }
            if (c == '\'' || c == '"') { inString = true; stringChar = c; sb.Append(c); continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                if (i < s.Length) sb.Append('\n');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>Counts and warnings produced by one <see cref="Import.Run"/> call.</summary>
public sealed class ImportReport
{
    public int LaunchGroups { get; set; }
    public int LaunchItems { get; set; }
    public int Events { get; set; }
    public int Daily { get; set; }
    public int Icons { get; set; }
    public int WidgetsWritten { get; set; }
    public List<string> Warnings { get; } = new();

    public override string ToString() =>
        $"launchGroups={LaunchGroups} launchItems={LaunchItems} events={Events} daily={Daily} icons={Icons} widgets={WidgetsWritten} warnings={Warnings.Count}"
        + (Warnings.Count > 0 ? " :: " + string.Join(" | ", Warnings) : "");
}
