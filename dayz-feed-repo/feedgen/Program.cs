using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DayZLauncher.Models;
using DayZLauncher.Services;


// ---------------------------------------------------------------------------------------------
// DayZ server-feed generator v2. Runs on a schedule (GitHub Action), produces servers.json.
//
// v2 policy change: a server the runner cannot reach is NOT dropped anymore. Datacenter
// reachability is not player reachability - strict firewalls and long routes made v1 silently
// drop real, good servers, and the launcher's reconciliation then removed them from users'
// lists. Now only PROVEN spam is dropped (population liars, procedural-name floods, dead-end
// dense subnets); unreachable-but-listed servers ship unverified and each user's launcher
// verifies them locally from where they actually play.
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


// ---- 1. Enumerate in slices; every slice unions into byId.
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


// v2: slice per EVERY observed map (not just the biggest), so niche-map servers can never be
// crowded out of a capped response. ~100 extra API calls per run - trivial against the quota.
foreach (var map in byId.Values
             .GroupBy(s => s.Map, StringComparer.OrdinalIgnoreCase)
             .Where(g => !string.IsNullOrWhiteSpace(g.Key))
             .OrderByDescending(g => g.Count())
             .Take(60)
             .Select(g => g.Key)
             .ToList())
{
    if (await Enumerate($@"\appid\221100\map\{map}") >= 25)
    {
        await Enumerate($@"\appid\221100\map\{map}\noplayers\1");
    }
}


Console.WriteLine($"enumerated (deduped): {byId.Count}");


// ---- 2. Convict the procedural-name flood (same tail-signature rule the launcher uses).
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


// ---- 3. Verify by direct query. Responders get fresh numbers, a mods list, and the
//         population cross-check (claims 20+ but can't produce a player list => spam, drop).
//         NON-responders are kept as unverified - see the header note.
// Single-IP farm rule - density PLUS uniformity, never density alone: shared-hosting boxes
// legitimately pack dozens of differently named customer servers behind one IP (a plain
// density rule mass-convicted thousands of real servers in testing). 25+ listings on one
// address where 15+ share a name template is a registration farm (the 271-listing QQ farm:
// one IP, sequential ports, two rotating names).
foreach (var ipGroup in byId.Values.GroupBy(s => s.Ip).Where(g => g.Count() >= 25))
{
    foreach (var sigGroup in ipGroup.GroupBy(s => NameSignature(s.Name)).Where(g => g.Count() >= 15))
    {
        foreach (var s in sigGroup) convicted.Add(s.Id);
    }
}
Console.WriteLine($"convicted after single-IP uniformity rule: {convicted.Count}");


var a2s = new A2SQueryService();
var candidates = byId.Values.Where(s => !convicted.Contains(s.Id)).ToList();
var verified = new ConcurrentBag<Server>();
var unverified = new ConcurrentBag<Server>();
var popSuspects = new ConcurrentBag<string>();
using var throttle = new SemaphoreSlim(192);


await Task.WhenAll(candidates.Select(async srv =>
{
    await throttle.WaitAsync();
    try
    {
        var live = await a2s.QueryServerAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 1500);
        if (!live.IsOnline)
        {
            // Unreachable from the datacenter; ship it unverified with its master-list data.
            srv.ModsVerified = false;
            unverified.Add(srv);
            return;
        }


        srv.PopVerified = true; // answered the runner directly - the meaning of "verified"
        srv.CurrentPlayers = live.CurrentPlayers > 127 ? 0 : live.CurrentPlayers;
        srv.MaxPlayers = live.MaxPlayers;
        if (!string.IsNullOrWhiteSpace(live.Map)) srv.Map = live.Map;
        if (!string.IsNullOrWhiteSpace(live.Name)) srv.Name = live.Name;
        if (!string.IsNullOrWhiteSpace(live.GameVersion)) srv.GameVersion = live.GameVersion;
        srv.RequiresPassword = live.RequiresPassword;


        if (srv.CurrentPlayers >= 20)
        {
            // A failed player-list check alone is NOT proof of spam: plenty of real hosts
            // answer the info query but block A2S_PLAYER outright, and the old drop-on-fail
            // rule was executing popular real servers (the live feed showed a cliff at
            // exactly 20 players). The row is only marked SUSPICIOUS here; conviction
            // happens later, and only when its IP/subnet is dense with fellow suspects -
            // a lone server that hides its player list survives, a rack of fake-populated
            // listings does not.
            int listed = await a2s.QueryPlayerListCountAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 1500);
            if (listed < 5)
            {
                popSuspects.Add(srv.Id);
            }
        }


        var (ids, _, ok) = await a2s.QueryModsAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 1500);
        srv.ModsVerified = ok;
        if (ok) srv.RequiredMods = ids;


        verified.Add(srv);
    }
    catch { }
    finally { throttle.Release(); }
}));


// Corroborated population-lie convictions: a suspicious row (big claim, no producible
// player list) is dropped only when 10+ suspects share its /24 - the signature of a farm,
// not of one privacy-minded host.
var suspectSet = new HashSet<string>(popSuspects, StringComparer.OrdinalIgnoreCase);
var suspectsBySubnet = verified.Where(s => suspectSet.Contains(s.Id))
    .GroupBy(SubnetOf)
    .Where(g => g.Count() >= 10)
    .SelectMany(g => g.Select(s => s.Id))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var verifiedKept = verified.Where(s => !suspectsBySubnet.Contains(s.Id)).ToList();
Console.WriteLine($"verified: {verified.Count} (pop-lie convicted: {verified.Count - verifiedKept.Count}), unverified kept: {unverified.Count}");


// ---- 4. Farm-subnet rule for the unverified pile. The flood's fingerprint is unmistakable
//         in the data: /24 subnets carrying THOUSANDS of listings (137.220.56.x had 4,176 -
//         a /24 only has 256 addresses) that are 0.0% populated. Real hosting racks always
//         have a populated fraction during active hours; a farm is a wall of empty
//         registrations. So an unverified 0-player row is dropped only when its /24 is BOTH
//         dense (25+ listings) AND essentially dead (<3% of its rows have any players) - a
//         guard that spared 750 real servers on legit racks in testing while still nuking
//         ~30k flood rows. A row that claims players (The Lab, KarmaKrew: unreachable from
//         the datacenter but reporting a real crowd) is always kept, as is any verified row.
var pool = verifiedKept.Concat(unverified).ToList();
var subnetCounts = pool.GroupBy(SubnetOf).ToDictionary(g => g.Key, g => g.Count());
var subnetPopFrac = pool.GroupBy(SubnetOf)
    .ToDictionary(g => g.Key, g => g.Count(s => s.CurrentPlayers > 0) / (double)g.Count());


bool IsFarmRow(Server s)
{
    string sn = SubnetOf(s);
    return s.CurrentPlayers == 0 && subnetCounts[sn] >= 25 && subnetPopFrac[sn] < 0.03;
}


var keptUnverified = unverified.Where(s => !IsFarmRow(s)).ToList();
Console.WriteLine($"unverified after farm-subnet rule: {keptUnverified.Count} (dropped {unverified.Count - keptUnverified.Count})");


// ---- 5. Publish. The "mods" field is present ONLY when the rules query answered, so the
//         launcher can distinguish verified-vanilla from unknown.
var all = verifiedKept.Concat(keptUnverified).OrderByDescending(s => s.CurrentPlayers).ToList();
var output = new
{
    // Contract version: from 2 upward, "mods" is present ONLY when the rules query answered,
    // so an empty array is a verified-vanilla statement. v1 feeds emitted the array always,
    // which made "unknown" and "vanilla" indistinguishable - clients must not vanilla-trust
    // a feed without this marker.
    formatVersion = 2,
    generatedAt = DateTime.UtcNow.ToString("o"),
    servers = all.Select(s => new
    {
        id = s.Id,
        name = s.Name,
        ip = s.Ip,
        port = s.Port,
        queryPort = s.QueryPort,
        map = s.Map,
        // Unverified rows only have SELF-REPORTED counts (the adaptive spam's favorite lie),
        // so the feed publishes 0 for them - clients rank on verified reality only, and each
        // user's local sweep supplies the real number the moment the row is looked at.
        players = s.PopVerified ? s.CurrentPlayers : 0,
        maxPlayers = s.MaxPlayers,
        official = s.IsOfficial,
        passworded = s.RequiresPassword,
        version = s.GameVersion,
        verified = s.PopVerified,
        mods = s.ModsVerified ? s.RequiredMods : null,
    }),
};


File.WriteAllText("servers.json", JsonSerializer.Serialize(output,
    new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
Console.WriteLine($"servers.json written: {all.Count} servers");
return verifiedKept.Count > 500 ? 0 : 2; // refuse to publish an implausibly small list
