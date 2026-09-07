using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// GET /graph?src=&lt;id&gt; — serves a graphify graph.json trimmed to the configured node/link caps.
/// Unlike the Python helper (which used per-source caps h:2000/e:100), a single MaxNodes/MaxLinks
/// pair applies to every source; this is an intentional simplification for the C# host.
/// </summary>
public sealed class GraphService : IHostService
{
    private sealed record CacheEntry(DateTime MtimeUtc, JsonObject Result);

    private readonly HostContext _ctx;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public GraphService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Register(LocalApi api) => api.Map("GET", "/graph", Handle);

    private Task Handle(ApiRequest req)
    {
        var src = req.Query("src");
        if (string.IsNullOrEmpty(src)) src = "h";

        if (!_ctx.Config.App.Graphs.TryGetValue(src, out var path) || string.IsNullOrEmpty(path) || !File.Exists(path))
            return req.Error(404, "no graph");

        JsonObject result;
        try
        {
            result = BuildOrGetCached(src, path);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("graph build failed for src=" + src, ex);
            return req.Error(404, "no graph");
        }

        return req.Text(result.ToJsonString(Json.Api), "application/json; charset=utf-8");
    }

    private JsonObject BuildOrGetCached(string src, string path)
    {
        var mtimeUtc = File.GetLastWriteTimeUtc(path);
        if (_cache.TryGetValue(src, out var cached) && cached.MtimeUtc == mtimeUtc)
            return cached.Result;

        var limits = _ctx.Config.App.GraphLimits;
        var result = Build(src, path, mtimeUtc, limits.MaxNodes, limits.MaxLinks);
        _cache[src] = new CacheEntry(mtimeUtc, result);
        return result;
    }

    private static JsonObject Build(string src, string path, DateTime mtimeUtc, int maxNodes, int maxLinks)
    {
        var raw = Json.LoadFile(path) as JsonObject ?? throw new InvalidOperationException("bad graph file");
        var rawNodes = raw["nodes"] as JsonArray ?? new JsonArray();
        var rawLinks = raw["links"] as JsonArray ?? new JsonArray();

        // Node degree: count of links touching each node id, computed over the full (untrimmed) graph.
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in rawLinks)
        {
            var s = AsString(l?["source"]);
            var t = AsString(l?["target"]);
            if (s is not null) degree[s] = degree.GetValueOrDefault(s) + 1;
            if (t is not null) degree[t] = degree.GetValueOrDefault(t) + 1;
        }

        var nodeEntries = rawNodes
            .Where(n => n is JsonObject)
            .Select(n => (Node: (JsonObject)n!, Id: AsString(n!["id"])))
            .Where(e => e.Id is not null)
            .ToList();

        if (nodeEntries.Count > maxNodes)
        {
            nodeEntries = nodeEntries
                .OrderByDescending(e => degree.GetValueOrDefault(e.Id!, 0))
                .Take(maxNodes)
                .ToList();
        }

        var keep = new HashSet<string>(nodeEntries.Select(e => e.Id!), StringComparer.Ordinal);

        var outNodes = new JsonArray();
        foreach (var (node, id) in nodeEntries)
        {
            var label = AsString(node["label"]);
            if (string.IsNullOrEmpty(label)) label = id;
            outNodes.Add(new JsonObject
            {
                ["id"] = id,
                ["label"] = label,
                ["type"] = AsString(node["file_type"]),
                ["file"] = AsString(node["source_file"]),
                ["community"] = node["community"]?.DeepClone(),
                ["degree"] = degree.GetValueOrDefault(id!, 0),
            });
        }

        var linkEntries = new List<(JsonObject Obj, double Weight)>();
        foreach (var l in rawLinks)
        {
            if (l is not JsonObject lo) continue;
            var s = AsString(lo["source"]);
            var t = AsString(lo["target"]);
            if (s is null || t is null || !keep.Contains(s) || !keep.Contains(t)) continue;

            var weightNode = lo["weight"];
            var weight = AsNumber(weightNode) ?? 1.0;
            linkEntries.Add((new JsonObject
            {
                ["source"] = s,
                ["target"] = t,
                ["w"] = weightNode?.DeepClone() ?? JsonValue.Create(1),
            }, weight));
        }

        if (linkEntries.Count > maxLinks)
        {
            linkEntries = linkEntries.OrderByDescending(e => e.Weight).Take(maxLinks).ToList();
        }

        var outLinks = new JsonArray();
        foreach (var (obj, _) in linkEntries) outLinks.Add(obj);

        var root = "";
        try
        {
            var rootFile = Path.Combine(Path.GetDirectoryName(path) ?? "", ".graphify_root");
            if (File.Exists(rootFile)) root = File.ReadAllText(rootFile).Trim();
        }
        catch { /* root file is optional cosmetic info */ }

        return new JsonObject
        {
            ["src"] = src,
            ["root"] = root,
            ["total_nodes"] = rawNodes.Count,
            ["total_links"] = rawLinks.Count,
            ["nodes"] = outNodes,
            ["links"] = outLinks,
            ["mtime"] = (mtimeUtc - DateTime.UnixEpoch).TotalSeconds,
        };
    }

    private static string? AsString(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return node.ToString();
    }

    private static double? AsNumber(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<double>(out var d)) return d;
        return null;
    }
}
