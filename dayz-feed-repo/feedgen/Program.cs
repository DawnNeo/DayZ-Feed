using System.Collections.Concurrent;
using System.Text.Json;
using DayZLauncher.Models;
using DayZLauncher.Services;

// ---------------------------------------------------------------------------------------------
// DayZ server-feed generator. Runs on a schedule (GitHub Action), produces servers.json:
// the complete master list, enumerated in slices past the ~10k cap, spam-convicted with the
// same evidence rules the launcher uses, each survivor verified by direct query.
//
// Requires STEAM_WEB_API_KEY in the environment (the OPERATOR's key - never shipped to users).
// ---------------------------------------------------------------------------------------------

string? apiKey = Environment.GetEnvironmentVariable("STEAM_WEB_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("STEAM_WEB_API_KEY is not set.");
    return 1;
}

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var byId = new Dictionary<string, Server>(StringComparer.OrdinalIgnoreCase);

// ---- 1. Enumerate the master list in slices (unfiltered + not-empty + empty + per-map splits).
async Task<int> Enumerate(string filter)
{
    int added = 0;
    string url = "https://api.steampowered.com/IGameServersService/GetServerList/v1/" +
                 $"?key={apiKey}&limit=20000&filter={Uri.EscapeDataString(filter)}";
    try
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("response", out var resp) ||
            !resp.TryGetProperty("servers", out var servers))
        {
            return 0;
        }

        foreach (var e in servers.EnumerateArray())
        {
            string addr = e.TryGetProperty("addr", out var a) ? a.GetString() ?? "" : "";
            int gamePort = e.TryGetProperty("gameport", out var gp) ? gp.GetInt32() : 0;
            int colon = addr.LastIndexOf(':');
            if (colon <= 0) continue;
            string ip = addr[..colon];
            int queryPort = int.TryParse(addr[(colon + 1)..], out var qp) ? qp : 0;
            if (gamePort <= 0) gamePort = queryPort > 0 ? queryPort - 1 : 0;
            if (gamePort <= 0) continue;

            string id = $"{ip}:{gamePort}";
            if (byId.ContainsKey(id)) continue;

            int players = e.TryGetProperty("players", out var p) ? p.GetInt32() : 0;
            byId[id] = new Server
            {
                Id = id,
                Ip = ip,
                Port = gamePort,
                QueryPort = queryPort,
                Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "DayZ Server" : "DayZ Server",
                Map = e.TryGetProperty("map", out var m) ? m.GetString() ?? "" : "",
                CurrentPlayers = players > 127 ? 0 : players,
                MaxPlayers = e.TryGetProperty("max_players", out var mp) ? mp.GetInt32() : 0,
                GameVersion = e.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                IsOfficial = e.TryGetProperty("gametype", out var gt) &&
                             System.Text.RegularExpressions.Regex.IsMatch(gt.GetString() ?? "", @"shard\d{3}(,|$)") &&
                             !(gt.GetString() ?? "").Contains("external", StringComparison.OrdinalIgnoreCase) &&
                             !(gt.GetString() ?? "").Contains("privHive", StringComparison.OrdinalIgnoreCase),
            };
            added++;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"slice '{filter}' failed: {ex.Message}");
    }
    Console.WriteLine($"slice '{filter}': +{added} (total {byId.Count})");
    return added;
}

await Enumerate(@"\appid\221100");
await Enumerate(@"\appid\221100\empty\1");     // classic filter semantics: NOT empty
await Enumerate(@"\appid\221100\noplayers\1"); // empty servers
foreach (var map in byId.Values.GroupBy(s => s.Map).OrderByDescending(g => g.Count()).Take(15).Select(g => g.Key))
{
    if (string.IsNullOrWhiteSpace(map)) continue;
    if (await Enumerate($@"\appid\221100\map\{map}") < 50) continue;
    await Enumerate($@"\appid\221100\map\{map}\noplayers\1");
}

Console.WriteLine($"enumerated (deduped): {byId.Count}");

// ---- 2. Convict the flood: name-template clustering (same tail signature as the launcher).
static string NameSignature(string name)
{
    var chars = new System.Text.StringBuilder(name.Length);
    foreach (char c in name)
    {
        if (char.IsDigit(c)) continue;
        chars.Append(char.ToLowerInvariant(c));
    }
    string collapsed = System.Text.RegularExpressions.Regex.Replace(chars.ToString(), @"\s+", " ").Trim();
    return collapsed.Length <= 30 ? collapsed : collapsed[^30..];
}

static string SubnetOf(Server s)
{
    int i = s.Ip.LastIndexOf('.');
    return i > 0 ? s.Ip[..i] : s.Ip;
}

var convicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var g in byId.Values.GroupBy(s => NameSignature(s.Name))
             .Where(g => g.Key.Length >= 8 && g.Count() >= 40)
             .Where(g => g.Select(SubnetOf).Distinct().Count() >= 10))
{
    foreach (var s in g) convicted.Add(s.Id);
}
Console.WriteLine($"template-convicted: {convicted.Count}");

// ---- 3. Verify the survivors by direct query (the backend has time; users don't).
//         A row that claims 20+ players must produce a player list; one that answers nothing
//         at all is dropped as dead/fake.
var a2s = new A2SQueryService();
var survivors = byId.Values.Where(s => !convicted.Contains(s.Id)).ToList();
var verified = new ConcurrentBag<Server>();
using var throttle = new SemaphoreSlim(64);

await Task.WhenAll(survivors.Select(async srv =>
{
    await throttle.WaitAsync();
    try
    {
        var live = await a2s.QueryServerAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 1500);
        if (!live.IsOnline) return;

        srv.CurrentPlayers = live.CurrentPlayers > 127 ? 0 : live.CurrentPlayers;
        srv.MaxPlayers = live.MaxPlayers;
        if (!string.IsNullOrWhiteSpace(live.Map)) srv.Map = live.Map;
        if (!string.IsNullOrWhiteSpace(live.Name)) srv.Name = live.Name;
        if (!string.IsNullOrWhiteSpace(live.GameVersion)) srv.GameVersion = live.GameVersion;
        srv.RequiresPassword = live.RequiresPassword;

        if (srv.CurrentPlayers >= 20)
        {
            int listed = await a2s.QueryPlayerListCountAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 1500);
            if (listed < 5) return; // claims a crowd, can't produce one - fake
        }

        var (ids, _, ok) = await a2s.QueryModsAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 1500);
        if (ok) srv.RequiredMods = ids;

        verified.Add(srv);
    }
    catch { }
    finally { throttle.Release(); }
}));

Console.WriteLine($"verified real servers: {verified.Count}");

// ---- 4. Publish.
var output = new
{
    generatedAt = DateTime.UtcNow.ToString("o"),
    servers = verified.OrderByDescending(s => s.CurrentPlayers).Select(s => new
    {
        id = s.Id,
        name = s.Name,
        ip = s.Ip,
        port = s.Port,
        queryPort = s.QueryPort,
        map = s.Map,
        players = s.CurrentPlayers,
        maxPlayers = s.MaxPlayers,
        official = s.IsOfficial,
        passworded = s.RequiresPassword,
        version = s.GameVersion,
        mods = s.RequiredMods,
    }),
};

File.WriteAllText("servers.json", JsonSerializer.Serialize(output));
Console.WriteLine("servers.json written");
return verified.Count > 500 ? 0 : 2; // refuse to publish an implausibly small list
