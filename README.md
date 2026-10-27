# DayZ-Feed

The server list behind the DZHarbor Launcher. A GitHub Action reads the full DayZ master list
from Steam, checks every server directly, flags the fake listings that flood the master list,
and publishes the result as one JSON file:

**https://dawnneo.github.io/DayZ-Feed/servers.json**

It also records each verified server's player count, which is what the launcher's population
charts are drawn from.

## How a run works

1. **Enumerate.** Pull the master list from the Steam Web API in slices (with players, empty,
   then per map) so no server is crowded out of a capped response.
2. **Blocklist.** Anything matching [`blocklist.txt`](blocklist.txt) is flagged as a suspect.
3. **Listing farms (master list).** Addresses that are obviously farms are flagged *before* any
   querying, so the run doesn't spend time on them (see the rules below).
4. **Country.** Each IP is resolved to a country code from the public-domain
   [geo-whois-asn-country](https://github.com/sapics/ip-location-db) dataset.
5. **Name rules and hearing.** Generated-name floods are flagged. Then every accused listing
   that claims players is asked for its player list, and a real one clears it.
6. **Verify.** Every remaining server is queried directly (A2S info, player list, mods).
   Servers that answer become `verified` with measured numbers. Servers that don't answer are
   still published, with their self-reported player count zeroed. The launcher checks those
   itself from the player's own connection.
7. **Spam rules.** Population liars, port impostors and dead farm subnets are flagged, the farm
   rules run again with measured numbers, servers that still can't list their claimed players
   on a third check 5+ minutes later are flagged, and copies that can't prove players are
   flagged. A player list whose online times climb in one constant step is generated, not real,
   and counts as no list.
8. **Publish.** `servers.json` is deployed to GitHub Pages, and the population history is
   force-pushed to the `data` branch.

A run takes about half an hour. The schedule asks for every 20 minutes, but GitHub runs
scheduled jobs on free accounts much less often in practice (every few hours). Use
**Actions → Publish DayZ server feed → Run workflow** to run it on demand.

## Suspects: flagged, never deleted

Listings judged fake are **not** removed. They are published in a separate `suspect` array
with the reason. The launcher hides them by default, but its "show everything" toggle still
lists them with a SUSPECT badge, and a player's favourites and played servers are always shown.
So a real server that gets wrongly flagged can still be found and joined, and it keeps its
history. Launchers older than 1.0.2 only read `servers`, so they never see suspects at all.

| `reason` | Meaning |
|---|---|
| `blocklist` | Matched an entry in `blocklist.txt`. |
| `farm-capacity` | The address has 25+ listings and at least half advertise more than DayZ's 127-slot limit. |
| `farm-clones` | The address has 50+ listings and at least half reuse a name that 3+ other addresses also use. |
| `farm-full-house` | The address has 50+ listings and every one with players claims to be exactly full. |
| `farm-dense` | The address has 50+ listings and 10+ of them have players. No proven-real address has more than 8 listings. |
| `frozen-list` | Claims 20+ players, but its player list came back identical 5+ minutes later: nobody's online time moved (official servers exempt). |
| `no-player-list` | Claims 20+ players but produced no real player list on three checks spread over 5+ minutes (official servers exempt). |
| `clone-no-list` | Its name is on 3+ addresses, it claims 20+ players but can't produce a player list, and an older listing with the same name exists (that original is never flagged by this rule). |
| `name-flood` | One of 40+ listings sharing a generated name pattern across 10+ subnets. |
| `ip-uniform-names` | Its address has 25+ listings, 15+ of them with the same name pattern. |
| `pop-lie` | Claims 20+ players but answered with an empty player list, and so did a neighbour on its /24. |
| `port-impostor` | Its query port is answered by a different server, and the address has other impostors. |
| `dead-farm-subnet` | An unreachable, empty listing on a /24 with 25+ listings where under 3% have players. |

The farm rules judge a whole **address** and only kick in at high density, because no real
machine runs dozens of DayZ servers on one IP. That density gate is what protects the real
server that a clone farm copies: the original sits on an address with a handful of listings.

**Real players clear a listing.** One player list proves nothing: some fakes replay a frozen
snapshot of 70–119 names. So every listing that answers with 5+ names is asked again at least
5 minutes later. If the same players' online times grew by the time that passed (or by a cache
step near it; some hosts refresh every ~100 s), the listing is proven real and cleared of every
farm, clone and population verdict (blocklist entries excepted), and published with
`"proven": true`. If the list is identical, it's flagged `frozen-list`. Measured live in
October 2026: 18 of 18 real servers advanced, and 71 of 71 suspected replay listings stayed frozen.

## blocklist.txt

One entry per line, `#` starts a comment:

```
1.2.3.4:2302    # that one listing
1.2.3.4         # every port on that address (for fakes that hop ports)
1.2.3.          # the whole /24
```

Matches are flagged as suspects with reason `blocklist`, never deleted.

## servers.json format

```jsonc
{
  "formatVersion": 2,
  "generatedAt": "2026-10-26T20:04:13Z",
  "servers": [ /* rows */ ],
  "suspect": [ /* rows, each with a "reason" */ ]
}
```

Each row:

| Field | Notes |
|---|---|
| `id` | `ip:gamePort`, the key everything uses. |
| `name`, `ip`, `port`, `queryPort`, `map`, `version` | From the server's own answer when verified, otherwise from the master list. |
| `players`, `maxPlayers` | `players` is the measured count for verified rows and `0` otherwise, including all suspects. |
| `verified` | The server answered the runner directly. |
| `official`, `passworded` | |
| `mods` | Workshop ids. **Present only when the mods query answered**, so `[]` means verified vanilla and a missing field means unknown. |
| `cc` | Country code (optional). |
| `fpp` | `true` = first-person only (optional; missing means unknown). |
| `since` | When this listing was first recorded in the history, ISO 8601 UTC (optional). |
| `proven` | `true` when two player-list samples 5+ minutes apart showed the same players still playing (optional). |
| `reason` | Suspects only. |

## Population history (`data` branch)

Sharded into `s00.json` … `s39.json`; a server's shard is the sum of the UTF-8 bytes of its
`id`, mod 40. Each server keeps `f` (fine samples for the last 48 hours: `[unixMinute, players]`),
`d` (up to 30 daily rows: `[date, sum, count, peak]`) and `fs` (the unix minute it was first
recorded, written once and never trimmed). Servers that stop being seen age out after 30 days.
The branch is replaced with a single commit every run, so it never grows.

## Setup

- **Secret:** `STEAM_WEB_API_KEY` under Settings → Secrets and variables → Actions. It stays
  on GitHub and is never published.
- **Pages:** Settings → Pages → Source: **GitHub Actions**. The workflow deploys the feed
  directly. It is not committed to the repository, which keeps the repo from growing every run.
