using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WiFi;
using Windows.Win32.System.Power;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// GET /system/network, /system/battery, /system/layout, /system/brightness, /system/dnd,
/// PUT /system/brightness, PUT /system/dnd, POST /system/power — the small always-on-top-bar
/// widgets that read/change desktop state instead of audio. Every read is computed on demand
/// (cheap Win32 calls; nothing here is worth caching), except network/battery which also feed the
/// "network-changed"/"battery-changed" events below.
/// </summary>
public sealed class SystemInfoService : IHostService, IDisposable
{
    private const int ThrottleMs = 200;
    private const int BatteryPollMs = 30_000;

    private readonly HostContext _ctx;
    private readonly EventsService _events;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _batteryLoop;

    private readonly object _throttleLock = new();
    private readonly Dictionary<string, DateTime> _lastEventAt = new();

    public SystemInfoService(HostContext ctx, EventsService events)
    {
        _ctx = ctx;
        _events = events;
        _ctx.Log.Info("system/dnd: not supported on this build (no reliable public Focus Assist API on Windows 11)");

        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _batteryLoop = Task.Run(() => BatteryLoopAsync(_cts.Token));
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/system/network", req => req.Json(ReadNetworkJson()));
        api.Map("GET", "/system/battery", req => req.Json(ReadBatteryJson()));
        api.Map("GET", "/system/layout", GetLayout);
        api.Map("GET", "/system/brightness", GetBrightness);
        api.Map("PUT", "/system/brightness", PutBrightness);
        api.Map("GET", "/system/dnd", req => req.Json(new { supported = false, on = false }));
        api.Map("PUT", "/system/dnd", req => req.Json(new { ok = false, error = "unsupported" }, 400));
        api.Map("POST", "/system/power", PostPower);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        try { _cts.Cancel(); } catch { }
        try { _batteryLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------ /system/network

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        BroadcastThrottled("network-changed", () => new JsonObject { ["type"] = "network-changed" });

    private static JsonObject ReadNetworkJson()
    {
        var info = ReadNetwork();
        return new JsonObject
        {
            ["up"] = info.Up,
            ["kind"] = info.Kind,
            ["name"] = info.Name,
            ["signal"] = info.Signal,
            ["ipv4"] = info.Ipv4,
        };
    }

    private sealed record NetworkInfo(bool Up, string Kind, string? Name, int? Signal, string? Ipv4);

    private static NetworkInfo ReadNetwork()
    {
        NetworkInterface? best = null;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); } catch { continue; }
                if (!props.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork)) continue;

                var hasGateway = props.GatewayAddresses.Count > 0;
                if (best is null || hasGateway) best = nic;
                if (hasGateway) break;
            }
        }
        catch { /* leave best null -> reported as down */ }

        if (best is null) return new NetworkInfo(false, "none", null, null, null);

        string? ipv4 = null;
        try
        {
            ipv4 = best.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
        }
        catch { /* address disappeared between the scan above and here */ }

        if (best.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
        {
            var wifi = Wlan.TryQueryCurrentConnection(best.Id);
            var name = wifi?.Ssid ?? best.Name;
            return new NetworkInfo(true, "wifi", name, wifi?.SignalQuality, ipv4);
        }

        return new NetworkInfo(true, "ethernet", best.Name, null, ipv4);
    }

    // ------------------------------------------------------------------ /system/battery

    private async Task BatteryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { BroadcastThrottled("battery-changed", () => new JsonObject { ["type"] = "battery-changed" }); }
            catch (Exception ex) { _ctx.Log.Error("battery poll", ex); }
            try { await Task.Delay(BatteryPollMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static unsafe JsonObject ReadBatteryJson()
    {
        SYSTEM_POWER_STATUS status;
        if (!PInvoke.GetSystemPowerStatus(&status))
        {
            return new JsonObject { ["present"] = false, ["percent"] = null, ["charging"] = false, ["remainingMin"] = null };
        }

        var flag = (byte)status.BatteryFlag;
        var present = flag != 128 && flag != 255;
        int? percent = status.BatteryLifePercent == 255 ? null : status.BatteryLifePercent;
        var charging = (flag & 8) != 0;
        int? remainingMin = status.BatteryLifeTime == 0xFFFFFFFF ? null : (int)(status.BatteryLifeTime / 60);

        return new JsonObject
        {
            ["present"] = present,
            ["percent"] = present ? percent : null,
            ["charging"] = charging,
            ["remainingMin"] = present ? remainingMin : null,
        };
    }

    // ------------------------------------------------------------------ /system/layout

    private static unsafe Task GetLayout(ApiRequest req)
    {
        var hwnd = PInvoke.GetForegroundWindow();
        uint threadId = 0;
        if (hwnd != default)
        {
            threadId = PInvoke.GetWindowThreadProcessId(hwnd, null);
        }
        var hkl = PInvoke.GetKeyboardLayout(threadId);
        var hklValue = (nint)hkl;
        var langId = (uint)hklValue & 0xFFFF;

        string lang = "??";
        try
        {
            if (langId != 0) lang = new CultureInfo((int)langId).TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch { /* unmapped LCID — leave the placeholder */ }

        return req.Json(new
        {
            lang,
            hkl = "0x" + hklValue.ToString("X8"),
        });
    }

    // ------------------------------------------------------------------ /system/brightness

    private static Task GetBrightness(ApiRequest req)
    {
        var monitors = Brightness.ReadAll();
        return req.Json(new
        {
            supported = monitors.Count > 0,
            monitors = monitors.Select(m => new { id = m.Id, name = m.Name, value = m.Value }),
        });
    }

    private async Task PutBrightness(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var node = Json.ParseNode(body) as JsonObject;
        if (node?["value"] is not JsonValue vv || !vv.TryGetValue(out int value))
        {
            await req.Error(400, "missing value").ConfigureAwait(false);
            return;
        }
        var id = node["id"]?.GetValue<string>();

        if (!Brightness.TrySet(id, value, out var error))
        {
            _ctx.Log.Warn("PUT /system/brightness failed: " + error);
            await req.Json(new { ok = false, error = "unsupported" }).ConfigureAwait(false);
            return;
        }
        await req.Json(new { ok = true, monitors = Brightness.ReadAll().Select(m => new { id = m.Id, name = m.Name, value = m.Value }) }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ /system/power

    private async Task PostPower(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var node = Json.ParseNode(body) as JsonObject;
        var action = node?["action"]?.GetValue<string>();
        var confirm = node?["confirm"] is JsonValue cv && cv.TryGetValue(out bool c) && c;

        try
        {
            switch (action)
            {
                case "lock":
                    PInvoke.LockWorkStation();
                    await req.Json(new { ok = true }).ConfigureAwait(false);
                    return;
                case "sleep":
                    PInvoke.SetSuspendState(false, false, false);
                    await req.Json(new { ok = true }).ConfigureAwait(false);
                    return;
                case "restart":
                case "shutdown":
                    if (!confirm)
                    {
                        await req.Json(new { ok = false, error = "confirm required" }, 400).ConfigureAwait(false);
                        return;
                    }
                    var args = action == "restart" ? "/r /t 0" : "/s /t 0";
                    Process.Start(new ProcessStartInfo("shutdown", args) { UseShellExecute = false, CreateNoWindow = true });
                    await req.Json(new { ok = true }).ConfigureAwait(false);
                    return;
                default:
                    await req.Json(new { ok = false, error = "bad action" }, 400).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("POST /system/power " + action, ex);
            await req.Json(new { ok = false, error = ex.Message }, 500).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ events throttling

    private void BroadcastThrottled(string type, Func<JsonObject> buildPayload)
    {
        lock (_throttleLock)
        {
            var now = DateTime.UtcNow;
            if (_lastEventAt.TryGetValue(type, out var last) && (now - last).TotalMilliseconds < ThrottleMs) return;
            _lastEventAt[type] = now;
        }
        try { _events.Broadcast(buildPayload()); }
        catch (Exception ex) { _ctx.Log.Error("system broadcast " + type, ex); }
    }

    // ------------------------------------------------------------------ Wi-Fi SSID/signal (wlanapi.dll)

    /// <summary>
    /// Reads the current connection's SSID and signal quality through wlanapi.dll
    /// (WlanOpenHandle/WlanEnumInterfaces/WlanQueryInterface with
    /// wlan_intf_opcode_current_connection). Every step is wrapped so a missing WLAN service, no
    /// adapter, or a disconnected interface just yields null instead of throwing.
    /// </summary>
    private static class Wlan
    {
        public sealed record Result(string? Ssid, int? SignalQuality);

        public static unsafe Result? TryQueryCurrentConnection(string interfaceId)
        {
            Guid.TryParse(interfaceId, out var wantGuid);

            try
            {
                if (PInvoke.WlanOpenHandle(2, out _, out var client) != 0) return null;
                using (client)
                {
                    if (PInvoke.WlanEnumInterfaces(client, out var pList) != 0 || pList == null) return null;
                    try
                    {
                        var items = pList->InterfaceInfo.AsSpan((int)pList->dwNumberOfItems);
                        if (items.Length == 0) return null;

                        var chosen = items[0].InterfaceGuid;
                        foreach (var info in items)
                        {
                            if (wantGuid != Guid.Empty && info.InterfaceGuid == wantGuid) { chosen = info.InterfaceGuid; break; }
                        }

                        if (PInvoke.WlanQueryInterface(client, chosen, WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection, out _, out var pData) != 0 || pData == null)
                            return null;
                        try
                        {
                            var attrs = *(WLAN_CONNECTION_ATTRIBUTES*)pData;
                            var ssidLen = (int)attrs.wlanAssociationAttributes.dot11Ssid.uSSIDLength;
                            string? ssid = null;
                            if (ssidLen is > 0 and <= 32)
                            {
                                var bytes = attrs.wlanAssociationAttributes.dot11Ssid.ucSSID.AsReadOnlySpan()[..ssidLen];
                                ssid = System.Text.Encoding.UTF8.GetString(bytes);
                            }
                            var signal = (int)attrs.wlanAssociationAttributes.wlanSignalQuality;
                            return new Result(ssid, Math.Clamp(signal, 0, 100));
                        }
                        finally { PInvoke.WlanFreeMemory(pData); }
                    }
                    finally { PInvoke.WlanFreeMemory(pList); }
                }
            }
            catch
            {
                // no WLAN service on this build, adapter driver issue, etc — treated as "no SSID info"
                return null;
            }
        }
    }
}
