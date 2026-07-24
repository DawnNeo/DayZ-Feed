using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DayZLauncher.Models;
using DayZLauncher.Services;


// ---------------------------------------------------------------------------------------------
// DayZ server-feed generator v4. Runs on a schedule (GitHub Action), produces servers.json.
//
// v4: two optional per-row fields. "cc" - country code resolved from the server's IP against
// the public-domain (CC0/PDDL) geo-whois-asn-country dataset, downloaded fresh each run, so
// the launcher's region filter needs no keys and no per-user lookups. "fpp" - true when the
// server's gametype tags carry "no3rd" (first-person only); present only when tags were seen,
// so absence means "unknown", never "3PP".
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
            string gametype = e.TryGetProperty("gametype", out var gt) ? gt.GetString() ?? "" : "";
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
                IsOfficial = gametype.Length > 0 &&
                             System.Text.RegularExpressions.Regex.IsMatch(gametype, @"shard\d{3}(,|$)") &&
                             !gametype.Contains("external", StringComparison.OrdinalIgnoreCase) &&
                             !gametype.Contains("privHive", StringComparison.OrdinalIgnoreCase),
                // Perspective rides in the same tags: "no3rd" = first person only. Tags being
                // present at all is what makes "no no3rd" a real 3PP statement, not silence.
                IsFirstPersonOnly = System.Text.RegularExpressions.Regex.IsMatch(gametype, @"\bno3rd\b"),
                PerspectiveKnown = gametype.Length > 0,
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


// ---- 1b. Country per server, resolved from its IP against the geo-whois-asn-country
//          dataset (CC0/PDDL - public domain, no keys, no attribution requirement, freely
//          redistributable). Downloaded fresh each run; a failed download just means this
//          run's feed ships without "cc" and the launcher's region filter sits idle.
try
{
    static uint IpNum(string ip)
    {
        var o = ip.Split('.');
        if (o.Length != 4) return 0;
        return byte.TryParse(o[0], out var a) && byte.TryParse(o[1], out var b) &&
               byte.TryParse(o[2], out var c) && byte.TryParse(o[3], out var d)
            ? ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d
            : 0;
    }


    string csv = await http.GetStringAsync(
        "https://cdn.jsdelivr.net/npm/@ip-location-db/geo-whois-asn-country/geo-whois-asn-country-ipv4-num.csv");


    // Rows are "startNum,endNum,CC", sorted ascending and non-overlapping.
    var starts = new List<uint>(300_000);
    var ends = new List<uint>(300_000);
    var codes = new List<string>(300_000);
    foreach (var line in csv.Split('\n'))
    {
        var parts = line.Trim().Split(',');
        if (parts.Length < 3) continue;
        if (!uint.TryParse(parts[0], out var s) || !uint.TryParse(parts[1], out var en)) continue;
        starts.Add(s);
        ends.Add(en);
        codes.Add(parts[2].Trim().ToUpperInvariant());
    }


    int resolved = 0;
    foreach (var srv in byId.Values)
    {
        uint n = IpNum(srv.Ip);
        if (n == 0) continue;


        // Binary search for the last range starting at or below n, then bounds-check it.
        int lo = 0, hi = starts.Count - 1, hit = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (starts[mid] <= n) { hit = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (hit >= 0 && n <= ends[hit] && codes[hit].Length == 2)
        {
            srv.Country = codes[hit];
            resolved++;
        }
    }
    Console.WriteLine($"country resolved: {resolved}/{byId.Count} ({starts.Count:N0} ranges)");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"country resolution failed (non-fatal): {ex.Message}");
}


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
// Claim-20+ rows whose player-list check got NO answer during the storm. The storm runs
// 192 queries wide, so a lost UDP exchange reads as innocent silence - and the July 2026
// flood wave hid ~1,700 fabricated-population rows behind exactly that: they answer the
// info query instantly but their player-list lie only surfaces on a calm retry.
var silentClaimers = new ConcurrentBag<Server>();
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
        // The server's own keywords answer outranks the master list's tags.
        if (live.PerspectiveKnown)
        {
            srv.IsFirstPersonOnly = live.IsFirstPersonOnly;
            srv.PerspectiveKnown = true;
        }


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
            // ANSWERED with nobody = the fake signature. Silence stays innocent (plenty of
            // real hosts block this query outright) - the same field-proven taxonomy the
            // launcher uses: real populated servers answer WITH entries, privacy hosts answer
            // NOTHING, only fakes answer WITH NOBODY.
            if (listed >= 0 && listed < 5)
            {
                popSuspects.Add(srv.Id);
            }
            else if (listed < 0)
            {
                silentClaimers.Add(srv); // re-heard calmly below; silence is never punished
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


// R1: calm re-hearing for claim-20+ rows whose player-list went unanswered in the storm.
// Low concurrency and a generous timeout, so a lost exchange can't hide the lie; the
// evidence rule is UNCHANGED - only an actual answer with nobody in it convicts.
{
    using var calm = new SemaphoreSlim(24);
    int reheard = 0, caught = 0;
    await Task.WhenAll(silentClaimers.Select(async srv =>
    {
        await calm.WaitAsync();
        try
        {
            int listed = await a2s.QueryPlayerListCountAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 2500);
            if (listed < 0)
            {
                listed = await a2s.QueryPlayerListCountAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 2500);
            }
            Interlocked.Increment(ref reheard);
            if (listed >= 0 && listed < 5)
            {
                popSuspects.Add(srv.Id);
                Interlocked.Increment(ref caught);
            }
        }
        catch { }
        finally { calm.Release(); }
    }));
    Console.WriteLine($"calm re-hearing: {reheard} silent claimers re-queried, {caught} answered-with-nobody");
}


// R2: impossible capacity. DayZ's engine hard-caps a server at 127 survivors, so a row
// CLAIMING players in a "130/200/255-slot" server is lying by physics - the July flood
// stamps 130-220 on its listings while every real franchise tops out at exactly 127.
// Empty rows with a big max are left alone (a misconfigured cfg is not a lie until the
// row claims a crowd it cannot hold).
int impossible = 0;
foreach (var s in verified.Concat(unverified))
{
    if (s.CurrentPlayers > 0 && s.MaxPlayers > 127)
    {
        popSuspects.Add(s.Id);
        impossible++;
    }
}
Console.WriteLine($"impossible-capacity claims: {impossible}");


// Corroborated population-lie convictions: a suspicious row (big claim, no producible
// player list - or a physically impossible one) is dropped only when 2+ suspects share
// its /24 - the signature of a farm pod, not of one privacy-minded host. Applies to the
// unverified pile too: heartbeat-only listings are where the flood parks its bulk.
var suspectSet = new HashSet<string>(popSuspects, StringComparer.OrdinalIgnoreCase);
var suspectsBySubnet = verified.Concat(unverified).Where(s => suspectSet.Contains(s.Id))
    .GroupBy(SubnetOf)
    .Where(g => g.Count() >= 2)
    .SelectMany(g => g.Select(s => s.Id))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var verifiedKept = verified.Where(s => !suspectsBySubnet.Contains(s.Id)).ToList();
var unverifiedAfterCluster = unverified.Where(s => !suspectsBySubnet.Contains(s.Id)).ToList();
Console.WriteLine($"verified: {verified.Count} (pop-lie convicted: {verified.Count - verifiedKept.Count}), " +
                  $"unverified: {unverified.Count} (pop-lie convicted: {unverified.Count - unverifiedAfterCluster.Count})");


// ---- 4. Farm-subnet rule for the unverified pile. The flood's fingerprint is unmistakable
//         in the data: /24 subnets carrying THOUSANDS of listings (137.220.56.x had 4,176 -
//         a /24 only has 256 addresses) that are 0.0% populated. Real hosting racks always
//         have a populated fraction during active hours; a farm is a wall of empty
//         registrations. So an unverified 0-player row is dropped only when its /24 is BOTH
//         dense (25+ listings) AND essentially dead (<3% of its rows have any players) - a
//         guard that spared 750 real servers on legit racks in testing while still nuking
//         ~30k flood rows. A row that claims players (The Lab, KarmaKrew: unreachable from
//         the datacenter but reporting a real crowd) is always kept, as is any verified row.
var pool = verifiedKept.Concat(unverifiedAfterCluster).ToList();
var subnetCounts = pool.GroupBy(SubnetOf).ToDictionary(g => g.Key, g => g.Count());
// The populated fraction counts only CREDIBLE population: a claim with an impossible
// capacity is not population. The flood defeated the v3 version of this rule by simply
// claiming players in its heartbeats - free lies must not keep a farm subnet "alive".
var subnetPopFrac = pool.GroupBy(SubnetOf)
    .ToDictionary(g => g.Key, g => g.Count(s => s.CurrentPlayers > 0 && s.MaxPlayers <= 127) / (double)g.Count());


bool IsFarmRow(Server s)
{
    string sn = SubnetOf(s);
    // A row with an impossible capacity gets no claims-players exemption either.
    return (s.CurrentPlayers == 0 || s.MaxPlayers > 127) &&
           subnetCounts[sn] >= 25 && subnetPopFrac[sn] < 0.03;
}


var keptUnverified = unverifiedAfterCluster.Where(s => !IsFarmRow(s)).ToList();
Console.WriteLine($"unverified after farm-subnet rule: {keptUnverified.Count} (dropped {unverifiedAfterCluster.Count - keptUnverified.Count})");


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
        // user's local sweep supplies the real number the moment the row is looked at. A
        // count in an impossible-capacity server (max > the 127 engine cap) is zeroed too.
        players = s.PopVerified && s.MaxPlayers <= 127 ? s.CurrentPlayers : 0,
        maxPlayers = s.MaxPlayers,
        official = s.IsOfficial,
        passworded = s.RequiresPassword,
        version = s.GameVersion,
        verified = s.PopVerified,
        mods = s.ModsVerified ? s.RequiredMods : null,
        // Optional v4 fields (omitted when unknown, thanks to WhenWritingNull below):
        // country code from the IP, and the first-person-only flag from the "no3rd" tag.
        cc = string.IsNullOrEmpty(s.Country) ? null : s.Country,
        fpp = s.PerspectiveKnown ? s.IsFirstPersonOnly : (bool?)null,
    }),
};


File.WriteAllText("servers.json", JsonSerializer.Serialize(output,
    new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
Console.WriteLine($"servers.json written: {all.Count} servers");


// ---- 6. Population history, recorded by US - this is how BattleMetrics-style charts exist
//         without BattleMetrics: every run appends each VERIFIED server's measured count.
//         The workflow checks prior state out into ./history (the repo's force-pushed "data"
//         branch, always a single commit so the repo never bloats) and pushes ./history-out.
//         Retention: 48h at run resolution ("fine") + 30 daily aggregates (sum,count,peak).
try
{
    const int ShardCount = 40;
    static int ShardOf(string id)
    {
        int h = 0;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(id)) h = (h + b) % 40;
        return h;
    }


    long nowMin = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60000;
    long fineCutoff = nowMin - 48 * 60;
    string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
    Directory.CreateDirectory("history-out");


    var byShard = verifiedKept.GroupBy(s => ShardOf(s.Id)).ToDictionary(g => g.Key, g => g.ToList());


    for (int shard = 0; shard < ShardCount; shard++)
    {
        var root = new System.Text.Json.Nodes.JsonObject();
        var servers = new System.Text.Json.Nodes.JsonObject();
        string prior = Path.Combine("history", $"s{shard:D2}.json");
        if (File.Exists(prior))
        {
            try
            {
                var parsed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(prior)) as System.Text.Json.Nodes.JsonObject;
                if (parsed?["servers"] is System.Text.Json.Nodes.JsonObject prev)
                {
                    servers = prev;
                    parsed.Remove("servers");
                }
            }
            catch { /* corrupted shard - start it fresh */ }
        }


        if (byShard.TryGetValue(shard, out var members))
        {
            foreach (var srv in members)
            {
                if (servers[srv.Id] is not System.Text.Json.Nodes.JsonObject entry)
                {
                    entry = new System.Text.Json.Nodes.JsonObject
                    {
                        ["f"] = new System.Text.Json.Nodes.JsonArray(),
                        ["d"] = new System.Text.Json.Nodes.JsonArray(),
                    };
                    servers[srv.Id] = entry;
                }


                var fine = entry["f"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
                fine.Add(new System.Text.Json.Nodes.JsonArray { nowMin, srv.CurrentPlayers });
                while (fine.Count > 0 && fine[0] is System.Text.Json.Nodes.JsonArray first &&
                       (long)(first[0] ?? 0L) < fineCutoff)
                {
                    fine.RemoveAt(0);
                }
                entry["f"] = fine;


                var daily = entry["d"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
                System.Text.Json.Nodes.JsonArray? todayRow = null;
                if (daily.Count > 0 && daily[^1] is System.Text.Json.Nodes.JsonArray lastRow &&
                    (string?)lastRow[0] == today)
                {
                    todayRow = lastRow;
                }
                if (todayRow == null)
                {
                    todayRow = new System.Text.Json.Nodes.JsonArray { today, 0L, 0L, 0L };
                    daily.Add(todayRow);
                    while (daily.Count > 30) daily.RemoveAt(0);
                }
                todayRow[1] = (long)(todayRow[1] ?? 0L) + srv.CurrentPlayers;
                todayRow[2] = (long)(todayRow[2] ?? 0L) + 1;
                todayRow[3] = Math.Max((long)(todayRow[3] ?? 0L), srv.CurrentPlayers);
                entry["d"] = daily;
            }
        }


        root["servers"] = servers;
        File.WriteAllText(Path.Combine("history-out", $"s{shard:D2}.json"), root.ToJsonString());
    }
    Console.WriteLine($"history shards written for {verifiedKept.Count} verified servers");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"history update failed (non-fatal): {ex.Message}");
}


return verifiedKept.Count > 500 ? 0 : 2; // refuse to publish an implausibly small list




