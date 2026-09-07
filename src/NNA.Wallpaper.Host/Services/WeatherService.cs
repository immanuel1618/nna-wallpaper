using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// GET /weather — Open-Meteo, no API key, 10-minute cache. On fetch failure, retries are allowed
/// after 60 seconds and the last good data (if any) is served in the meantime, same as the Python
/// helper's <c>weather()</c>.
/// </summary>
public sealed class WeatherService : IHostService, IDisposable
{
    // Same table as helper.py's WMO dict, verbatim.
    private static readonly Dictionary<int, string> Wmo = new()
    {
        [0] = "CLEAR", [1] = "MAINLY CLEAR", [2] = "PARTLY CLOUDY", [3] = "OVERCAST",
        [45] = "FOG", [48] = "RIME FOG",
        [51] = "LIGHT DRIZZLE", [53] = "DRIZZLE", [55] = "HEAVY DRIZZLE",
        [56] = "FREEZING DRIZZLE", [57] = "FREEZING DRIZZLE",
        [61] = "LIGHT RAIN", [63] = "RAIN", [65] = "HEAVY RAIN",
        [66] = "FREEZING RAIN", [67] = "FREEZING RAIN",
        [71] = "LIGHT SNOW", [73] = "SNOW", [75] = "HEAVY SNOW", [77] = "SNOW GRAINS",
        [80] = "SHOWERS", [81] = "SHOWERS", [82] = "HEAVY SHOWERS",
        [85] = "SNOW SHOWERS", [86] = "SNOW SHOWERS",
        [95] = "THUNDERSTORM", [96] = "HAIL STORM", [99] = "HAIL STORM",
    };

    private const double CacheSeconds = 600;
    private const double RetryAfterSeconds = 60;
    private const string UnknownText = "—"; // em dash, matches Python's '—'

    private readonly HostContext _ctx;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _lock = new(1, 1);

    private double _cacheAtUnix;
    private JsonObject? _cacheData;

    public WeatherService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Register(LocalApi api) => api.Map("GET", "/weather", Handle);

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }

    private async Task Handle(ApiRequest req)
    {
        var data = await GetWeatherAsync().ConfigureAwait(false);
        await req.Text(data.ToJsonString(Json.Api), "application/json; charset=utf-8").ConfigureAwait(false);
    }

    private async Task<JsonObject> GetWeatherAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = UnixNow();
            if (_cacheData is not null && now - _cacheAtUnix < CacheSeconds)
                return _cacheData;

            var w = _ctx.Config.App.Weather;
            var url = "https://api.open-meteo.com/v1/forecast" +
                      "?latitude=" + w.Lat.ToString(CultureInfo.InvariantCulture) +
                      "&longitude=" + w.Lon.ToString(CultureInfo.InvariantCulture) +
                      "&current=temperature_2m,apparent_temperature,relative_humidity_2m,wind_speed_10m,weather_code,is_day" +
                      "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                      "&timezone=" + Uri.EscapeDataString(w.Tz) +
                      "&forecast_days=4&wind_speed_unit=ms";

            try
            {
                using var resp = await _http.GetAsync(url).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var raw = Json.ParseNode(body) as JsonObject ?? throw new InvalidOperationException("bad weather response");

                var data = BuildData(raw, w.Name, now);
                _cacheData = data;
                _cacheAtUnix = now;
                return data;
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("weather fetch failed", ex);
                _cacheAtUnix = now - (CacheSeconds - RetryAfterSeconds);
                if (_cacheData is not null) return _cacheData;

                var msg = ex.Message;
                if (msg.Length > 80) msg = msg[..80];
                return new JsonObject { ["ok"] = false, ["name"] = w.Name, ["error"] = msg };
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static JsonObject BuildData(JsonObject raw, string name, double now)
    {
        var cur = raw["current"] as JsonObject ?? new JsonObject();
        var daily = raw["daily"] as JsonObject ?? new JsonObject();

        var times = daily["time"] as JsonArray ?? new JsonArray();
        var codes = daily["weather_code"] as JsonArray ?? new JsonArray();
        var maxes = daily["temperature_2m_max"] as JsonArray ?? new JsonArray();
        var mins = daily["temperature_2m_min"] as JsonArray ?? new JsonArray();

        var days = new JsonArray();
        for (var i = 0; i < times.Count; i++)
        {
            var dayCode = i < codes.Count ? AsInt(codes[i]) : null;
            days.Add(new JsonObject
            {
                ["date"] = times[i]?.DeepClone(),
                ["code"] = dayCode is int dc ? dc : null,
                ["text"] = TextFor(dayCode),
                ["max"] = i < maxes.Count ? maxes[i]?.DeepClone() : null,
                ["min"] = i < mins.Count ? mins[i]?.DeepClone() : null,
            });
        }

        var code = AsInt(cur["weather_code"]);
        return new JsonObject
        {
            ["ok"] = true,
            ["name"] = name,
            ["ts"] = now,
            ["temp"] = cur["temperature_2m"]?.DeepClone(),
            ["feels"] = cur["apparent_temperature"]?.DeepClone(),
            ["humidity"] = cur["relative_humidity_2m"]?.DeepClone(),
            ["wind_ms"] = cur["wind_speed_10m"]?.DeepClone(),
            ["code"] = code is int c ? c : null,
            ["text"] = TextFor(code),
            ["is_day"] = cur["is_day"]?.DeepClone(),
            ["days"] = days,
        };
    }

    private static string TextFor(int? code) => code.HasValue && Wmo.TryGetValue(code.Value, out var t) ? t : UnknownText;

    private static int? AsInt(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }

    private static double UnixNow() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
}
