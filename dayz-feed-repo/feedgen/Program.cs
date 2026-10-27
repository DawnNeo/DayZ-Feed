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


// Listings judged fake by the blocklist or the farm rules. They are NOT deleted: they are
// published in a separate "suspect" array with the reason, which launchers hide by default but
// can still reveal. A wrongly accused real server therefore stays findable and joinable, and its
// history is kept. Older launchers only read "servers", so they never see these at all.
var suspects = new List<(Server Server, string Reason)>();


// ---- 1a. Operator blocklist (blocklist.txt at the repo root). The automatic rules below are
//          deliberately conservative - shared RU hosting runs many genuinely full servers on
//          random high ports, so a rule strict enough to catch every fake also hides real
//          servers. This is the deterministic override for the ones that slip through.
//          One entry per line: "1.2.3.4:2302" = that listing, "1.2.3.4" = every port on that
//          address (port-hopping fakes re-register on a new port every few days), "1.2.3." =
//          the whole /24. "#" starts a comment. Matches go to the suspect list, unqueried.
{
    var exactIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var addresses = new HashSet<string>(StringComparer.Ordinal);
    var prefixes = new List<string>();
    if (File.Exists("blocklist.txt"))
    {
        foreach (var raw in File.ReadAllLines("blocklist.txt"))
        {
            string entry = raw.Split('#')[0].Trim();
            if (entry.Length == 0) continue;
            if (entry.EndsWith('.')) prefixes.Add(entry);
            else if (entry.Contains(':')) exactIds.Add(entry);
            else addresses.Add(entry);
        }
    }

    var blocked = byId.Values
        .Where(s => exactIds.Contains(s.Id) || addresses.Contains(s.Ip) ||
                    prefixes.Any(p => s.Ip.StartsWith(p, StringComparison.Ordinal)))
        .Select(s => s.Id)
        .ToList();
    foreach (var id in blocked)
    {
        suspects.Add((byId[id], "blocklist"));
        byId.Remove(id);
    }
    Console.WriteLine($"operator blocklist: {exactIds.Count + addresses.Count + prefixes.Count} entries, flagged {blocked.Count} listing(s)");
}


// ---- 1a'. Listing farms, judged on the master list BEFORE any querying. The capacity and
//           clone signatures (see FindFarmIps) need only what the master list already says -
//           slot count, name, address - and these farms were ~45% of all listings, every one of
//           which used to be queried (and re-queried) only to be thrown away at the end. The
//           full-house signature needs measured populations, so it runs again after querying.
{
    var farms = FindFarmIps(byId.Values.ToList(), measured: false);
    var flagged = byId.Values.Where(s => farms.ContainsKey(s.Ip)).ToList();
    foreach (var s in flagged)
    {
        suspects.Add((s, farms[s.Ip]));
        byId.Remove(s.Id);
    }
    Console.WriteLine($"listing farms (master list): {farms.Count} address(es) " +
                      $"({string.Join(", ", farms.Values.GroupBy(r => r).Select(g => $"{g.Key} {g.Count()}"))}), " +
                      $"flagged {flagged.Count} listing(s) without querying; {byId.Count} left to verify");
}


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
    // Suspects get a country too, so the region filter still works if a player reveals them.
    foreach (var srv in byId.Values.Concat(suspects.Select(x => x.Server)))
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


// id -> reason. Convicted listings are not queried; they go to the suspect list after the
// hearing below (never deleted).
var convicted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var g in byId.Values.GroupBy(s => NameSignature(s.Name))
             .Where(g => g.Key.Length >= 8 && g.Count() >= 40)
             .Where(g => g.Select(SubnetOf).Distinct().Count() >= 10))
{
    foreach (var s in g) convicted[s.Id] = "name-flood";
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
        foreach (var s in sigGroup) convicted.TryAdd(s.Id, "ip-uniform-names");
    }
}
Console.WriteLine($"convicted after single-IP uniformity rule: {convicted.Count}");


var a2s = new A2SQueryService();


// ---- 2b. Hearing. Every listing judged on master-list evidence alone (farm addresses, name
//          floods) that CLAIMS players is asked for its player list before being flagged. A
//          list with 5+ entries is proof of real players - the one thing the fakes have never
//          produced - so that listing is cleared and verified like any other. Blocklist
//          entries are the operator's explicit call and are not re-heard.
{
    var accused = suspects.Where(x => x.Reason != "blocklist").Select(x => x.Server)
        .Concat(convicted.Keys.Select(id => byId[id]))
        .Where(s => s.CurrentPlayers >= 5)
        .ToList();

    // One attempt each, wide, and a hard time budget: the accused are ~45% of the master list
    // and most never answer, so an unbounded hearing could push the run past its 50-minute
    // limit and publish nothing. Anything not heard in time simply stays a suspect - the safe
    // default, since suspects are hidden, not deleted.
    using var hearingThrottle = new SemaphoreSlim(192);
    var cleared = new ConcurrentBag<Server>();
    int closed = 0;
    var hearing = Task.WhenAll(accused.Select(async srv =>
    {
        await hearingThrottle.WaitAsync();
        try
        {
            if (Volatile.Read(ref closed) != 0) return;
            int listed = await a2s.QueryPlayerListCountAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 2000, attempts: 1);
            if (Volatile.Read(ref closed) != 0) return;
            srv.PlayerListCount = Math.Max(srv.PlayerListCount, listed);
            if (srv.HasRealPlayerList) cleared.Add(srv);
        }
        catch { }
        finally { hearingThrottle.Release(); }
    }));
    bool finished = await Task.WhenAny(hearing, Task.Delay(TimeSpan.FromMinutes(8))) == hearing;
    Interlocked.Exchange(ref closed, 1);
    if (!finished) Console.WriteLine("hearing: time budget reached - unheard listings stay suspects");

    var clearedIds = cleared.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    suspects.RemoveAll(x => clearedIds.Contains(x.Server.Id));
    foreach (var srv in cleared)
    {
        convicted.Remove(srv.Id);
        byId[srv.Id] = srv;
    }

    // Everything still convicted is flagged now, unqueried.
    foreach (var (id, reason) in convicted) suspects.Add((byId[id], reason));
    Console.WriteLine($"hearing: {accused.Count} accused listing(s) claiming players asked for a player list, " +
                      $"{cleared.Count} cleared by a real list; {suspects.Count} suspect(s) so far");
}

var candidates = byId.Values.Where(s => !convicted.ContainsKey(s.Id)).ToList();
var verified = new ConcurrentBag<Server>();
var unverified = new ConcurrentBag<Server>();
var popSuspects = new ConcurrentBag<string>();
// Claim-20+ rows whose player-list check got NO answer during the storm. The storm runs
// 192 queries wide, so a lost UDP exchange reads as innocent silence - and the July 2026
// flood wave hid ~1,700 fabricated-population rows behind exactly that: they answer the
// info query instantly but their player-list lie only surfaces on a calm retry.
var silentClaimers = new ConcurrentBag<Server>();
// Listings whose advertised query port is answered by a different instance - see the guard
// inside the verification pass.
var impostors = new ConcurrentBag<Server>();
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


        // Port-impostor guard. A genuine server answers its own query port with its own game
        // port (EDF 0x80). The flood registers listings whose query ports point at OTHER
        // servers on the same box, so a naive verifier collects a real answer and stamps this
        // listing "verified" with someone else's name, map and population - which is how
        // fabricated rows were reaching users as backend-measured truth. An answer that
        // identifies a different game port proves nothing about THIS listing, so it is
        // discarded and the row is recorded as an impostor.
        // Adjacent ports are excused: game and query ports differ by one either way by
        // convention, and rows whose game port the master list omitted carry an inferred value.
        if (live.Port != live.QueryPort && srv.Port > 0 && Math.Abs(live.Port - srv.Port) > 1)
        {
            impostors.Add(srv);
            srv.ModsVerified = false;
            srv.CurrentPlayers = 0;
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
            // Never downgrade within a run: a lost exchange must not erase a list already heard.
            srv.PlayerListCount = Math.Max(srv.PlayerListCount, listed);
            if (srv.HasRealPlayerList) listed = srv.PlayerListCount;
            // ANSWERED with nobody = the fake signature. Silence on one check proves nothing on
            // its own - a lost exchange looks the same - so every short or silent claimer is
            // re-heard calmly below, and only a failure that repeats over 5+ minutes counts
            // (R5b). (Much of the "silence" earlier versions excused was really the split-reply
            // bug hiding every busy server's list; with that fixed, real servers answer.)
            if (!srv.HasRealPlayerList)
            {
                srv.FailedListChecks = 1;
                srv.FirstFailedListCheckAt = DateTime.UtcNow;
                silentClaimers.Add(srv);
            }
            if (listed >= 0 && listed < 5)
            {
                popSuspects.Add(srv.Id);
            }
        }


        var (ids, _, ok) = await a2s.QueryModsAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 1500);
        srv.ModsVerified = ok;
        if (ok) srv.RequiredMods = ids;


        verified.Add(srv);
    }
    catch
    {
        // Never lose a listing to an exception: it ships unverified, like an unreachable one.
        // (Both Add calls above are the last statement on their paths, so no double add.)
        srv.ModsVerified = false;
        unverified.Add(srv);
    }
    finally { throttle.Release(); }
}));


// R1: calm re-hearing for claim-20+ rows whose player list came back silent or short in the
// storm. Low concurrency and a generous timeout, so a lost exchange can't hide the lie. An
// answer with nobody in it feeds the pop-lie rule; any failure here is the second strike
// toward R5b.
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
            srv.PlayerListCount = Math.Max(srv.PlayerListCount, listed);
            if (srv.HasRealPlayerList) listed = srv.PlayerListCount;
            else srv.FailedListChecks++;
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
    Console.WriteLine($"calm re-hearing: {reheard} short/silent claimers re-queried, {caught} answered-with-nobody");
}


// R1b: port-stealing farms. One impostor can be a stale registration (a server that moved
// ports), so a lone one only loses its numbers. Two or more on the same ADDRESS is a farm
// registering listings that point at other people's servers - every impostor there is dropped.
var impostorList = impostors.ToList();
var stealingIps = impostorList.GroupBy(s => s.Ip, StringComparer.Ordinal)
    .Where(g => g.Count() >= 2)
    .Select(g => g.Key)
    .ToHashSet(StringComparer.Ordinal);
var impostorDrop = impostorList.Where(s => stealingIps.Contains(s.Ip))
    .Select(s => s.Id)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
Console.WriteLine($"port impostors: {impostorList.Count} on {impostorList.Select(s => s.Ip).Distinct().Count()} address(es); " +
                  $"dropping {impostorDrop.Count} from {stealingIps.Count} port-stealing address(es)");


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
var suspectsBySubnet = verified.Concat(unverified).Where(s => suspectSet.Contains(s.Id) && !s.HasRealPlayerList)
    .GroupBy(SubnetOf)
    .Where(g => g.Count() >= 2)
    .SelectMany(g => g.Select(s => s.Id))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
foreach (var s in verified.Concat(unverified).Where(s => suspectsBySubnet.Contains(s.Id))) suspects.Add((s, "pop-lie"));
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


foreach (var s in unverifiedAfterCluster)
{
    if (impostorDrop.Contains(s.Id)) suspects.Add((s, "port-impostor"));
    else if (IsDeadFarmRow(s)) suspects.Add((s, "dead-farm-subnet"));
}
// A listing that proved real players in the hearing keeps that proof even if it went quiet later.
bool IsDeadFarmRow(Server s) => IsFarmRow(s) && !s.HasRealPlayerList;
var keptUnverified = unverifiedAfterCluster.Where(s => !IsDeadFarmRow(s) && !impostorDrop.Contains(s.Id)).ToList();
Console.WriteLine($"unverified after farm-subnet rule: {keptUnverified.Count} (flagged {unverifiedAfterCluster.Count - keptUnverified.Count})");


// R4: uniform-claim clusters. The flood stamps ONE template across hundreds of ports on a
// single address - same map, same capacity, a "population" in a narrow band (observed live:
// 45.82.14.21 carrying ~100 listings, all sakhal, all 127-slot, all claiming 70-80 players).
// Real customers sharing a box run different maps, caps and populations. Members that the
// runner never verified lose their claimed numbers; verified rows are untouched, so a genuine
// rack that happens to be uniform keeps its measured counts.
{
    int refused = 0;
    foreach (var ipGroup in keptUnverified.Concat(verifiedKept).GroupBy(s => s.Ip, StringComparer.Ordinal)
                 .Where(g => g.Count() >= 25))
    {
        foreach (var config in ipGroup.GroupBy(s => (s.Map, s.MaxPlayers)))
        {
            var claimers = config.Where(s => !s.PopVerified && s.CurrentPlayers > 0).ToList();
            if (claimers.Count < 8) continue;
            foreach (var s in claimers)
            {
                s.CurrentPlayers = 0;
                refused++;
            }
        }
    }
    Console.WriteLine($"uniform-claim rule: refused {refused} template-cluster population claims");
}


// R5: listing farms, second pass. Same rules as the master-list pass (1a'), now with measured
// numbers: live slot counts, and the full-house signature, which needs real populations.
{
    var pool5 = verifiedKept.Concat(keptUnverified).ToList();
    var farms = FindFarmIps(pool5, measured: true);

    // A listing that produced a real player list stays, even on a farm address - the address
    // verdict is about the listings that can't prove anything.
    bool Flag5(Server s) => farms.ContainsKey(s.Ip) && !s.HasRealPlayerList;
    foreach (var s in pool5.Where(Flag5)) suspects.Add((s, farms[s.Ip]));
    verifiedKept = verifiedKept.Where(s => !Flag5(s)).ToList();
    keptUnverified = keptUnverified.Where(s => !Flag5(s)).ToList();
    Console.WriteLine($"listing farms (measured): {farms.Count} address(es) " +
                      $"({string.Join(", ", farms.Values.GroupBy(r => r).Select(g => $"{g.Key} {g.Count()}"))}), " +
                      $"flagged {pool5.Count - verifiedKept.Count - keptUnverified.Count} listing(s)");
}


// R5b: no player list, confirmed. A server that claims 20+ players but cannot list them on
// three checks spread over at least 5 minutes - the storm, the calm re-hearing, and this one -
// is flagged. Calibrated live (October 2026): across 810 checks of 135 real servers the
// failure never repeated like that, while single checks do occasionally come back empty on
// real hosts (Sunnyvale #9: 0 entries, then 8 a minute later). Each final check also re-reads
// the claim, so a server whose crowd simply left is not judged. Official servers are exempt.
// Runs before the copy rule so a flagged farm can never be anyone's "original".
{
    var pending = verifiedKept.Where(s => s.FailedListChecks >= 2 && !s.HasRealPlayerList && !s.IsOfficial).ToList();
    var confirmed = new ConcurrentBag<Server>();
    using var finalThrottle = new SemaphoreSlim(64);
    int closed5b = 0;

    var finalChecks = Task.WhenAll(pending.Select(async srv =>
    {
        // At least 5 minutes after the first failure, so one bad network moment can't
        // account for all three.
        var due = (srv.FirstFailedListCheckAt ?? DateTime.UtcNow) + TimeSpan.FromMinutes(5) - DateTime.UtcNow;
        if (due > TimeSpan.Zero) await Task.Delay(due);

        await finalThrottle.WaitAsync();
        try
        {
            if (Volatile.Read(ref closed5b) != 0) return;
            var live = await a2s.QueryServerAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 2500);
            if (!live.IsOnline || live.CurrentPlayers < 20) return; // claim gone - no judgment

            int listed = await a2s.QueryPlayerListCountAsync(srv.Ip, srv.EffectiveQueryPort, timeoutMs: 2500);
            srv.PlayerListCount = Math.Max(srv.PlayerListCount, listed);
            if (!srv.HasRealPlayerList && Volatile.Read(ref closed5b) == 0) confirmed.Add(srv);
        }
        catch { }
        finally { finalThrottle.Release(); }
    }));
    bool done5b = await Task.WhenAny(finalChecks, Task.Delay(TimeSpan.FromMinutes(10))) == finalChecks;
    Interlocked.Exchange(ref closed5b, 1);

    var noList = confirmed.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var s in verifiedKept.Where(s => noList.Contains(s.Id))) suspects.Add((s, "no-player-list"));
    verifiedKept = verifiedKept.Where(s => !noList.Contains(s.Id)).ToList();
    Console.WriteLine($"no-player-list: {pending.Count} claimer(s) failed twice, {noList.Count} failed a third time 5+ min later and were flagged" +
                      (done5b ? "" : " (time budget reached - unchecked ones stay published)"));
}


// When each listing was first recorded, from the population history (30 days deep). Used to
// tell an original from its copies, and published as "since" so launchers can do the same.
// Source: each history entry's permanent "fs" stamp (unix minute, written once and never
// trimmed - see step 6). Entries written before "fs" existed fall back to their earliest kept
// sample, which the 30-day trim keeps sliding forward. A damaged shard is skipped on its own;
// any other failure leaves firstSeen EMPTY, so the clone rule idles rather than judging on a
// partial picture.
var firstSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
try
{
    int badShards = 0;
    foreach (var file in Directory.Exists("history") ? Directory.GetFiles("history", "s*.json") : Array.Empty<string>())
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("servers", out var shardServers)) continue;
            foreach (var entry in shardServers.EnumerateObject())
            {
                var at = EarliestSample(entry.Value);
                if (at != null) firstSeen[entry.Name] = at.Value;
            }
        }
        catch
        {
            badShards++;
        }
    }
    Console.WriteLine($"first-seen dates loaded for {firstSeen.Count} listing(s)" + (badShards > 0 ? $"; {badShards} unreadable shard(s) skipped" : ""));
}
catch (Exception ex)
{
    firstSeen.Clear();
    Console.Error.WriteLine($"first-seen load failed (non-fatal, clone rule idles): {ex.Message}");
}


// A history entry's first-seen moment: its "fs" stamp, else the earliest sample it still keeps.
static DateTime? EarliestSample(JsonElement entry)
{
    if (entry.TryGetProperty("fs", out var fs) && fs.ValueKind == JsonValueKind.Number)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(fs.GetInt64() * 60000).UtcDateTime;
    }

    DateTime? earliest = null;
    if (entry.TryGetProperty("d", out var daily) && daily.ValueKind == JsonValueKind.Array && daily.GetArrayLength() > 0 &&
        daily[0].ValueKind == JsonValueKind.Array && daily[0].GetArrayLength() > 0 &&
        daily[0][0].ValueKind == JsonValueKind.String &&
        DateTime.TryParse(daily[0][0].GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var day))
    {
        earliest = day;
    }
    if (entry.TryGetProperty("f", out var fine) && fine.ValueKind == JsonValueKind.Array && fine.GetArrayLength() > 0 &&
        fine[0].ValueKind == JsonValueKind.Array && fine[0].GetArrayLength() > 0 &&
        fine[0][0].ValueKind == JsonValueKind.Number)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(fine[0][0].GetInt64() * 60000).UtcDateTime;
        if (earliest == null || at < earliest) earliest = at;
    }
    return earliest;
}


// R6: copies that can't prove players. The clone farms also run at low density (30-ish
// listings per address on 185.130.251.x, under the farm rules' bar): 20 of 20 such listings
// sampled live claimed 100-121 players and answered no player list, while the real servers
// they copy answer with their players (KarmaKrew #3 NA: 101 listed). Measured on the October
// 2026 feed: 99 candidates, 96 flagged, 3 cleared by real player lists (SAYONARA x2, HOTLINE).
// A listing is flagged when ALL of these hold:
//   - its name is carried by 3+ addresses (copies included),
//   - it claims 20+ players measured from its own answer,
//   - it did NOT produce a real player list (silent, or answered with fewer than 5),
//   - and another listing with the same name was first seen at least 2 days earlier - so the
//     ORIGINAL, the longest-standing listing of a name, is never flagged by this rule, even
//     when it hides its player list. A listing with no history of its own is never flagged.
{
    static string NameKey6(string n) => System.Text.RegularExpressions.Regex.Replace(n.ToLowerInvariant(), @"\s+", " ").Trim();

    // Copies already flagged count toward "carried by 3+ addresses" - but only UNFLAGGED listings
    // can be the original. An old copy on a farm address must never make the real server look
    // like the newcomer (measured: the real Rearmed EU4 would otherwise have been accused).
    var published = verifiedKept.Concat(keptUnverified).ToList();
    var ipsPerName = published.Concat(suspects.Select(x => x.Server))
        .GroupBy(s => NameKey6(s.Name))
        .ToDictionary(g => g.Key, g => g.Select(s => s.Ip).Distinct(StringComparer.Ordinal).Count());
    var oldestPerName = published.Where(s => firstSeen.ContainsKey(s.Id))
        .GroupBy(s => NameKey6(s.Name))
        .ToDictionary(g => g.Key, g => g.Min(s => firstSeen[s.Id]));

    bool IsCopy(Server s)
    {
        if (!s.PopVerified || s.CurrentPlayers < 20 || s.HasRealPlayerList) return false;
        string key = NameKey6(s.Name);
        if (key.Length < 8 || ipsPerName[key] < 3) return false;
        if (!oldestPerName.TryGetValue(key, out var oldest)) return false;
        // No history of its own = not judged. A real original can lack history (unreachable
        // from the runner for a while, or the history was reset); a genuinely new copy gets
        // recorded this run and is judged on the next one.
        if (!firstSeen.TryGetValue(s.Id, out var mine)) return false;
        return mine - oldest >= TimeSpan.FromDays(2);
    }

    var copies = verifiedKept.Where(IsCopy).ToList();
    foreach (var s in copies) suspects.Add((s, "clone-no-list"));
    var copyIds = copies.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    verifiedKept = verifiedKept.Where(s => !copyIds.Contains(s.Id)).ToList();
    Console.WriteLine($"clone rule: flagged {copies.Count} newer copy listing(s) that claimed 20+ players without a player list");
}


// One-address listing farms. No real machine runs 50+ DayZ servers behind one IP (each instance
// needs its own cores and several GB of RAM), yet the October 2026 feed carried 73 addresses with
// 50-290 listings each - nearly half the published list. Three signatures convict the whole
// address; every real server spot-checked (Rearmed, MIDNIGHT, PODPIVAS, DayZERO, DayZed.GG)
// lives elsewhere and is untouched.
//   A. 25+ listings, at least half advertising more than the engine's 127 slots - word-salad
//      names ("Polar Polar Divide Divide Survivor's Refuge") on cloud VPS addresses.
//   B. 50+ listings, at least half carrying a name 3+ OTHER addresses also carry - clones of
//      popular servers. Proven live: 8 "GROZA 7" copies queried at the same moment reported
//      8 different populations (103-127), which one real server cannot do.
//   C. 50+ listings where every measured populated listing is exactly full (the "80/80" farm).
//      Only with measured=true: master-list counts are self-reported and prove nothing.
//   D. 50+ listings with 10+ measured as populated ("farm-dense"). Measured only, too.
// The density gate is what protects originals: the real server a clone copies sits on an
// address with a handful of listings, so only the copies are flagged. Returns address -> reason.
static Dictionary<string, string> FindFarmIps(List<Server> pool, bool measured)
{
    static string NameKey(string n) => System.Text.RegularExpressions.Regex.Replace(n.ToLowerInvariant(), @"\s+", " ").Trim();

    var addressesPerName = pool.GroupBy(s => NameKey(s.Name))
        .ToDictionary(g => g.Key, g => g.Select(s => s.Ip).Distinct(StringComparer.Ordinal).Count());

    var farms = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var g in pool.GroupBy(s => s.Ip, StringComparer.Ordinal))
    {
        int n = g.Count();
        if (n >= 25 && g.Count(s => s.MaxPlayers > 127) * 2 >= n)
        {
            farms[g.Key] = "farm-capacity";
            continue;
        }
        if (n < 50) continue;

        int cloned = g.Count(s => { var k = NameKey(s.Name); return k.Length >= 8 && addressesPerName[k] - 1 >= 3; });
        if (cloned * 2 >= n)
        {
            farms[g.Key] = "farm-clones";
            continue;
        }

        if (!measured) continue;
        var populated = g.Where(s => s.PopVerified && s.CurrentPlayers > 0).ToList();
        if (populated.Count >= 10 && populated.All(s => s.CurrentPlayers == s.MaxPlayers))
        {
            farms[g.Key] = "farm-full-house";
        }
        // 50+ listings with 10+ of them showing players: no real machine runs that many busy
        // DayZ servers on one IP (no proven-real address in the October 2026 feed had more than
        // 8 listings). Caught 31.77.78.108 - 125 copies just under the clones bar. Listings on
        // it that produce a real player list are still spared (R5).
        else if (populated.Count >= 10)
        {
            farms[g.Key] = "farm-dense";
        }
    }
    return farms;
}


// ---- 5. Publish. The "mods" field is present ONLY when the rules query answered, so the
//         launcher can distinguish verified-vanilla from unknown.
var all = verifiedKept.Concat(keptUnverified).OrderByDescending(s => s.CurrentPlayers).ToList();

object Row(Server s, string? reason) => new
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
    // count in an impossible-capacity server (max > the 127 engine cap) is zeroed too, and
    // so is every suspect's: if one is revealed, the launcher's own query gives the truth.
    players = reason == null && s.PopVerified && s.MaxPlayers <= 127 ? s.CurrentPlayers : 0,
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
    // First recorded in the population history (UTC, ISO 8601), so launchers can tell an
    // original from its copies with the same precision the feed uses. Omitted with no history.
    since = firstSeen.TryGetValue(s.Id, out var seen) ? seen.ToString("yyyy-MM-ddTHH:mm'Z'") : null,
    // A real player list (5+ entries) this run: proof the launcher honours over every rule.
    proven = s.HasRealPlayerList ? true : (bool?)null,
    // Suspects only: why it was flagged (see the README for every reason).
    reason,
};

var output = new
{
    // Contract version: from 2 upward, "mods" is present ONLY when the rules query answered,
    // so an empty array is a verified-vanilla statement. v1 feeds emitted the array always,
    // which made "unknown" and "vanilla" indistinguishable - clients must not vanilla-trust
    // a feed without this marker.
    formatVersion = 2,
    generatedAt = DateTime.UtcNow.ToString("o"),
    servers = all.Select(s => Row(s, null)),
    // Listings judged fake, kept rather than deleted (see the "suspects" note above). Clients
    // that read this hide them by default; clients that don't simply never see them.
    suspect = suspects.Select(x => Row(x.Server, x.Reason)),
};


File.WriteAllText("servers.json", JsonSerializer.Serialize(output,
    new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
Console.WriteLine($"servers.json written: {all.Count} servers, {suspects.Count} suspects");


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

                // Permanent first-seen stamp (unix minute): written once, never trimmed, gone only
                // when the whole entry ages out. The 30-day trim made every long-running listing
                // look equally old, which let month-old copies pass for originals. Entries from
                // before this existed are seeded from their earliest kept sample.
                if (entry["fs"] is null)
                {
                    entry["fs"] = firstSeen.TryGetValue(srv.Id, out var fsAt)
                        ? new DateTimeOffset(fsAt, TimeSpan.Zero).ToUnixTimeMilliseconds() / 60000
                        : nowMin;
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


        // Age out servers that weren't sampled this run. Retention was only ever applied to
        // a server as it was sampled, so a server that disappeared kept its last 48h and 30
        // days forever and the shards only grew. Expired samples go; an entry with nothing
        // left goes. Suspects are NOT deleted early - a wrongly flagged server keeps its
        // history for the normal 30 days.
        string oldestDay = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var sampled = members?.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                      ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in servers.Select(kv => kv.Key).ToList())
        {
            if (sampled.Contains(id) || servers[id] is not System.Text.Json.Nodes.JsonObject stale) continue;

            if (stale["f"] is System.Text.Json.Nodes.JsonArray f)
            {
                while (f.Count > 0 && f[0] is System.Text.Json.Nodes.JsonArray first && (long)(first[0] ?? 0L) < fineCutoff)
                {
                    f.RemoveAt(0);
                }
            }
            if (stale["d"] is System.Text.Json.Nodes.JsonArray d)
            {
                while (d.Count > 0 && d[0] is System.Text.Json.Nodes.JsonArray first &&
                       string.CompareOrdinal((string?)first[0] ?? "", oldestDay) < 0)
                {
                    d.RemoveAt(0);
                }
            }

            bool empty = (stale["f"] as System.Text.Json.Nodes.JsonArray)?.Count is null or 0 &&
                         (stale["d"] as System.Text.Json.Nodes.JsonArray)?.Count is null or 0;
            if (empty) servers.Remove(id);
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






