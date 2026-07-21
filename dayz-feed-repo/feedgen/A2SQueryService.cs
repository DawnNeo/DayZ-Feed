using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using DayZLauncher.Models;

namespace DayZLauncher.Services
{
    public class A2SQueryService
    {
        private static readonly byte[] RequestHeader = { 0xFF, 0xFF, 0xFF, 0xFF, 0x54, 0x53, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x20, 0x45, 0x6E, 0x67, 0x69, 0x6E, 0x65, 0x20, 0x51, 0x75, 0x65, 0x72, 0x79, 0x00 };

        /// <summary>
        /// Sends an A2S_INFO query and returns what the server reported.
        ///
        /// <paramref name="queryPort"/> must be the server's *query* port, not its game port -
        /// callers previously passed the game port, which nothing listens on, so every live
        /// refresh timed out and marked the whole list offline.
        ///
        /// The exchange retries once on timeout. UDP drops packets as a matter of course, and a
        /// single-shot query meant one lost datagram showed a perfectly healthy server as
        /// offline with no ping - the "keeps saying offline even though the server isn't down"
        /// flapping came straight from this.
        /// </summary>
        public async Task<Server> QueryServerAsync(string ipAddress, int queryPort, int timeoutMs = 2000, int attempts = 2)
        {
            Server result = MakeOffline(ipAddress, queryPort);

            for (int attempt = 0; attempt < Math.Max(1, attempts); attempt++)
            {
                result = await QueryOnceAsync(ipAddress, queryPort, timeoutMs);
                if (result.IsOnline) return result;
            }

            return result;
        }

        private static Server MakeOffline(string ipAddress, int queryPort) => new()
        {
            Ip = ipAddress,
            Port = queryPort,
            QueryPort = queryPort,
            Id = $"{ipAddress}:{queryPort}",
            IsOnline = false
        };

        private async Task<Server> QueryOnceAsync(string ipAddress, int queryPort, int timeoutMs)
        {
            var server = MakeOffline(ipAddress, queryPort);

            if (!IPAddress.TryParse(ipAddress, out var ip))
            {
                try
                {
                    var hosts = await Dns.GetHostAddressesAsync(ipAddress);
                    if (hosts.Length > 0) ip = hosts[0];
                    else return server;
                }
                catch
                {
                    return server;
                }
            }

            var endPoint = new IPEndPoint(ip, queryPort);
            using var udpClient = new UdpClient();
            udpClient.Client.SendTimeout = timeoutMs;
            udpClient.Client.ReceiveTimeout = timeoutMs;

            var stopwatch = Stopwatch.StartNew();

            try
            {
                await udpClient.SendAsync(RequestHeader, RequestHeader.Length, endPoint);

                byte[]? data = await ReceiveWithTimeoutAsync(udpClient, timeoutMs);
                if (data == null) return server; // timed out

                stopwatch.Stop();
                server.Ping = (int)stopwatch.ElapsedMilliseconds;

                if (data.Length < 5) return server;

                // A challenge means "ask again, but include this token". The old code re-sent the
                // request but then kept reading from the *original* buffer, so it parsed the
                // 9-byte challenge packet as though it were a full info response and produced
                // garbage for every server using challenges - which is most of them now.
                // Some servers also rotate the token and challenge twice in a row, so this loops
                // rather than assuming a single round.
                for (int round = 0; round < 2 && GetResponseType(data) == 0x41; round++)
                {
                    byte[] challengeRequest = new byte[RequestHeader.Length + 4];
                    Buffer.BlockCopy(RequestHeader, 0, challengeRequest, 0, RequestHeader.Length);
                    Buffer.BlockCopy(data, 5, challengeRequest, RequestHeader.Length, 4);

                    stopwatch.Restart();
                    await udpClient.SendAsync(challengeRequest, challengeRequest.Length, endPoint);

                    data = await ReceiveWithTimeoutAsync(udpClient, timeoutMs);
                    if (data == null || data.Length < 5) return server;

                    stopwatch.Stop();
                    server.Ping = (int)stopwatch.ElapsedMilliseconds;
                }

                if (GetResponseType(data) != 0x49) return server;

                ParseInfoResponse(data, server);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"A2S query error on {ipAddress}:{queryPort}: {ex.Message}");
            }

            return server;
        }

        /// <summary>
        /// Reads one datagram, giving up after <paramref name="timeoutMs"/>. Split ("multi-packet")
        /// responses are rejected rather than misparsed - A2S_INFO fits in a single packet for
        /// DayZ, and treating a fragment as a whole response yields nonsense fields.
        /// </summary>
        private static async Task<byte[]?> ReceiveWithTimeoutAsync(UdpClient client, int timeoutMs)
        {
            var receiveTask = client.ReceiveAsync();
            var timeoutTask = Task.Delay(timeoutMs);

            // Both tasks are held in locals: the old code compared the completed task against a
            // freshly-created Task.Delay, which is never the same object, so the timeout branch
            // could never be taken.
            var completed = await Task.WhenAny(receiveTask, timeoutTask);
            if (completed != receiveTask) return null;

            var result = await receiveTask;
            byte[] buffer = result.Buffer;

            if (buffer.Length < 5) return null;
            // 0xFFFFFFFF is a single-packet response; 0xFFFFFFFE marks a split one.
            if (BitConverter.ToInt32(buffer, 0) != -1) return null;

            return buffer;
        }

        private static byte GetResponseType(byte[] data) => data.Length >= 5 ? data[4] : (byte)0;

        private void ParseInfoResponse(byte[] data, Server server)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            reader.ReadInt32();          // header (0xFFFFFFFF)
            reader.ReadByte();           // response type (0x49)
            reader.ReadByte();           // protocol version

            server.IsOnline = true;
            server.Name = ReadNullTerminatedString(reader);
            server.Map = ReadNullTerminatedString(reader);
            ReadNullTerminatedString(reader); // folder
            ReadNullTerminatedString(reader); // game
            reader.ReadInt16();               // Steam app id
            server.CurrentPlayers = reader.ReadByte();
            server.MaxPlayers = reader.ReadByte();
            reader.ReadByte();                // bots
            reader.ReadByte();                // server type
            reader.ReadByte();                // environment
            reader.ReadByte();                // visibility (password required)
            reader.ReadByte();                // VAC

            server.GameVersion = ReadNullTerminatedString(reader);

            // Extra Data Flag block - DayZ publishes its mod list in the keywords field.
            if (ms.Position < ms.Length)
            {
                byte edf = reader.ReadByte();
                if ((edf & 0x80) != 0)
                {
                    // The server's actual GAME port. Rows sourced from Steam's master list only
                    // know the query port, so this is what makes their JOIN button connect to
                    // the right place instead of a guess.
                    ushort gamePort = (ushort)reader.ReadInt16();
                    if (gamePort > 0) server.Port = gamePort;
                }
                if ((edf & 0x10) != 0) reader.ReadInt64();  // SteamID
                if ((edf & 0x40) != 0)                      // spectator port + name
                {
                    reader.ReadInt16();
                    ReadNullTerminatedString(reader);
                }
                if ((edf & 0x20) != 0)                      // keywords
                {
                    string keywords = ReadNullTerminatedString(reader);
                    ParseKeywordsForMods(keywords, server);
                }
            }
        }

        /// <summary>
        /// Reads a null-terminated A2S string, decoding as UTF-8. The old cast-each-byte-to-char
        /// approach mangled any server name containing non-ASCII characters, which is extremely
        /// common in the DayZ browser (accented letters, box-drawing decoration, emoji).
        /// </summary>
        private string ReadNullTerminatedString(BinaryReader reader)
        {
            var bytes = new System.Collections.Generic.List<byte>(64);
            try
            {
                byte b;
                while ((b = reader.ReadByte()) != 0)
                {
                    bytes.Add(b);
                }
            }
            catch (EndOfStreamException)
            {
                // Truncated packet - return what we managed to read.
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private void ParseKeywordsForMods(string keywords, Server server)
        {
            // DayZ servers publish details in the keyword field, usually comma-separated, with
            // mod tags like "mod:1559212036". E.g.:
            //   "mod:1559212036,2233971631,2285132641,g:1.25,pvp,firstperson"
            if (string.IsNullOrEmpty(keywords)) return;

            var mods = new System.Collections.Generic.List<string>(server.RequiredMods);

            string[] tokens = keywords.Split(',', ';');
            foreach (var token in tokens)
            {
                string clean = token.Trim();
                if (clean.StartsWith("mod:", StringComparison.OrdinalIgnoreCase))
                {
                    string modPart = clean.Substring(4);
                    string[] ids = modPart.Split('_'); // some launchers split by underscore
                    foreach (var id in ids)
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d+$") && !mods.Contains(id))
                        {
                            mods.Add(id);
                        }
                    }
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(clean, @"^\d{9,11}$")) // bare workshop IDs
                {
                    if (!mods.Contains(clean)) mods.Add(clean);
                }
            }

            server.RequiredMods = mods;
        }

        #region A2S_RULES - DayZ mod list

        /// <summary>
        /// Asks the server itself for its mod list via A2S_RULES. DayZ packs the list into the
        /// rules response as escaped binary chunks (the DZSA wire format), which is the ONLY
        /// place mods exist for servers BattleMetrics doesn't index - Steam's master list
        /// carries no mod data at all.
        ///
        /// Returns Ok=false when the server didn't answer or the payload wasn't the DayZ binary
        /// format; Ok=true with empty lists is a *proven vanilla* server.
        /// </summary>
        public async Task<(System.Collections.Generic.List<string> Ids, System.Collections.Generic.List<string> Names, bool Ok)> QueryModsAsync(
            string ipAddress, int queryPort, int timeoutMs = 2500, int attempts = 2)
        {
            var none = (new System.Collections.Generic.List<string>(), new System.Collections.Generic.List<string>(), false);

            // Same retry policy as the info query: the exchange is several datagrams long and
            // losing any one of them (routinely, on UDP) must not read as "no mods".
            for (int attempt = 0; attempt < Math.Max(1, attempts); attempt++)
            {
                var result = await QueryModsOnceAsync(ipAddress, queryPort, timeoutMs);
                if (result.Ok) return result;
            }
            return none;
        }

        private async Task<(System.Collections.Generic.List<string> Ids, System.Collections.Generic.List<string> Names, bool Ok)> QueryModsOnceAsync(
            string ipAddress, int queryPort, int timeoutMs)
        {
            var none = (new System.Collections.Generic.List<string>(), new System.Collections.Generic.List<string>(), false);

            if (!IPAddress.TryParse(ipAddress, out var ip)) return none;
            var endPoint = new IPEndPoint(ip, queryPort);

            using var udpClient = new UdpClient();

            try
            {
                // A2S_RULES with the modern challenge dance: first request carries -1, the
                // server replies 0x41 + token, the request is repeated with the token.
                byte[] request = { 0xFF, 0xFF, 0xFF, 0xFF, 0x56, 0xFF, 0xFF, 0xFF, 0xFF };
                await udpClient.SendAsync(request, request.Length, endPoint);

                byte[]? data = await ReceiveWithTimeoutAsync(udpClient, timeoutMs);
                if (data == null || data.Length < 9) return none;

                // Only a single-packet (-1) reply can be a challenge; on a split (-2) reply
                // byte 4 is part of the fragment id and matching it against 0x41 is noise.
                for (int round = 0; round < 2 && data.Length >= 9 &&
                     BitConverter.ToInt32(data, 0) == -1 && GetResponseType(data) == 0x41; round++)
                {
                    Buffer.BlockCopy(data, 5, request, 5, 4);
                    await udpClient.SendAsync(request, request.Length, endPoint);
                    data = await ReceiveWithTimeoutAsync(udpClient, timeoutMs);
                    if (data == null || data.Length < 5) return none;
                }

                byte[]? payload = await ReassembleAsync(udpClient, data, timeoutMs);
                if (payload == null || payload.Length < 7 || payload[4] != 0x45) return none;

                return ParseDayZRules(payload);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"A2S rules query error on {ipAddress}:{queryPort}: {ex.Message}");
                return none;
            }
        }

        /// <summary>
        /// Counts the entries a server actually lists via A2S_PLAYER. Fake near-cap listings
        /// answer A2S_INFO with a fabricated population but cannot produce the player list to
        /// match; a genuinely full server lists dozens of survivors (DayZ truncates the list,
        /// so the result is a floor, not the population). Returns -1 when there is no valid
        /// reply at all.
        /// </summary>
        public async Task<int> QueryPlayerListCountAsync(string ipAddress, int queryPort, int timeoutMs = 2000, int attempts = 2)
        {
            for (int attempt = 0; attempt < Math.Max(1, attempts); attempt++)
            {
                int count = await QueryPlayerListOnceAsync(ipAddress, queryPort, timeoutMs);
                if (count >= 0) return count;
            }
            return -1;
        }

        private async Task<int> QueryPlayerListOnceAsync(string ipAddress, int queryPort, int timeoutMs)
        {
            if (!IPAddress.TryParse(ipAddress, out var ip)) return -1;
            var endPoint = new IPEndPoint(ip, queryPort);
            using var udpClient = new UdpClient();

            try
            {
                byte[] request = { 0xFF, 0xFF, 0xFF, 0xFF, 0x55, 0xFF, 0xFF, 0xFF, 0xFF };
                await udpClient.SendAsync(request, request.Length, endPoint);

                byte[]? data = await ReceiveWithTimeoutAsync(udpClient, timeoutMs);
                if (data == null || data.Length < 5) return -1;

                for (int round = 0; round < 2 && data.Length >= 9 &&
                     BitConverter.ToInt32(data, 0) == -1 && GetResponseType(data) == 0x41; round++)
                {
                    Buffer.BlockCopy(data, 5, request, 5, 4);
                    await udpClient.SendAsync(request, request.Length, endPoint);
                    data = await ReceiveWithTimeoutAsync(udpClient, timeoutMs);
                    if (data == null || data.Length < 5) return -1;
                }

                byte[]? payload = await ReassembleAsync(udpClient, data, timeoutMs);
                if (payload == null || payload.Length < 6 || payload[4] != 0x44) return -1;

                // Count the entries actually present ([index u8][name cstr][score i32][time f32])
                // rather than trusting the count byte, which a liar controls too.
                int pos = 6, entries = 0;
                while (pos < payload.Length)
                {
                    pos++; // player index
                    while (pos < payload.Length && payload[pos] != 0) pos++;
                    pos += 1 + 8; // terminator + score + duration
                    if (pos <= payload.Length) entries++;
                }
                return entries;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"A2S player query error on {ipAddress}:{queryPort}: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Handles Source-engine split responses (header -2). A modded DayZ rules reply rarely
        /// fits one datagram, so fragments are collected, ordered by their sequence number and
        /// concatenated. Single-packet responses (header -1) pass straight through.
        /// </summary>
        private static async Task<byte[]?> ReassembleAsync(UdpClient client, byte[] first, int timeoutMs)
        {
            int header = BitConverter.ToInt32(first, 0);
            if (header == -1) return first;
            if (header != -2) return null;

            var fragments = new System.Collections.Generic.SortedDictionary<int, byte[]>();
            int total;
            byte[] packet = first;

            while (true)
            {
                if (packet.Length < 12 || BitConverter.ToInt32(packet, 0) != -2) return null;

                int id = BitConverter.ToInt32(packet, 4);
                if ((id & unchecked((int)0x80000000)) != 0) return null; // bz2-compressed - not used by DayZ

                total = packet[8];
                int number = packet[9];
                // Source split header ends with a u16 max-fragment-size before the payload.
                fragments[number] = packet[12..];

                if (fragments.Count >= total) break;

                byte[]? next = await ReceiveWithTimeoutAsync(client, timeoutMs);
                if (next == null) return null;
                packet = next;
            }

            using var ms = new MemoryStream();
            foreach (var kv in fragments) ms.Write(kv.Value);
            byte[] whole = ms.ToArray();
            return whole.Length >= 5 && BitConverter.ToInt32(whole, 0) == -1 ? whole : null;
        }

        /// <summary>
        /// Decodes DayZ's binary rules payload. The mod list is spread across rules whose keys
        /// are two raw bytes: those values are concatenated in key order, unescaped (0x01 0x01 ->
        /// 0x01, 0x01 0x02 -> 0x00, 0x01 0x03 -> 0xFF - the escaping exists so binary data can
        /// survive the null-terminated rule framing), then read as: version byte, overflow flags,
        /// DLC bitfield + one u32 per set bit, mod count, and per mod a u32 hash, a length-tagged
        /// little-endian workshop id and a length-prefixed UTF-8 name.
        /// </summary>
        private static (System.Collections.Generic.List<string>, System.Collections.Generic.List<string>, bool) ParseDayZRules(byte[] payload)
        {
            var ids = new System.Collections.Generic.List<string>();
            var names = new System.Collections.Generic.List<string>();

            // Split the standard A2S_RULES framing: u16 rule count, then per rule a
            // null-terminated key and null-terminated value - both raw bytes here.
            int pos = 7; // 4 header + 0x45 + u16 count
            int count = BitConverter.ToUInt16(payload, 5);
            var binaryRules = new System.Collections.Generic.SortedDictionary<int, byte[]>();

            for (int i = 0; i < count && pos < payload.Length; i++)
            {
                byte[] key = ReadRawCString(payload, ref pos);
                byte[] value = ReadRawCString(payload, ref pos);

                // DayZ's data rules have exactly two raw bytes as their key; anything longer is
                // an ordinary text rule (allowedBuild etc.) and irrelevant here.
                if (key.Length == 2)
                {
                    binaryRules[key[0] | (key[1] << 8)] = value;
                }
            }

            if (binaryRules.Count == 0) return (ids, names, false);

            using var ms = new MemoryStream();
            foreach (var kv in binaryRules) ms.Write(kv.Value);
            byte[] blob = Unescape(ms.ToArray());

            try
            {
                int p = 0;
                byte version = blob[p++];
                if (version != 2) return (ids, names, false);

                p++; // overflow flags

                ushort dlcFlags = (ushort)(blob[p] | (blob[p + 1] << 8));
                p += 2;
                p += 4 * System.Numerics.BitOperations.PopCount(dlcFlags); // one u32 per DLC

                byte modCount = blob[p++];
                for (int i = 0; i < modCount; i++)
                {
                    p += 4; // per-mod hash

                    int idLen = blob[p++] & 0x0F;
                    ulong workshopId = 0;
                    for (int b = 0; b < idLen; b++) workshopId |= (ulong)blob[p + b] << (8 * b);
                    p += idLen;

                    int nameLen = blob[p++];
                    string name = Encoding.UTF8.GetString(blob, p, nameLen);
                    p += nameLen;

                    if (workshopId > 0)
                    {
                        ids.Add(workshopId.ToString());
                        names.Add(name);
                    }
                }

                return (ids, names, true);
            }
            catch (IndexOutOfRangeException)
            {
                return (new System.Collections.Generic.List<string>(), new System.Collections.Generic.List<string>(), false);
            }
            catch (ArgumentOutOfRangeException)
            {
                return (new System.Collections.Generic.List<string>(), new System.Collections.Generic.List<string>(), false);
            }
        }

        private static byte[] ReadRawCString(byte[] data, ref int pos)
        {
            int start = pos;
            while (pos < data.Length && data[pos] != 0) pos++;
            byte[] result = data[start..pos];
            if (pos < data.Length) pos++; // consume the terminator
            return result;
        }

        /// <summary>Single-pass unescape - a chained string replace corrupts payloads where an
        /// escaped 0x01 is followed by a literal 0x02 or 0x03.</summary>
        private static byte[] Unescape(byte[] data)
        {
            using var ms = new MemoryStream(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == 0x01 && i + 1 < data.Length)
                {
                    byte next = data[i + 1];
                    if (next == 0x01) { ms.WriteByte(0x01); i++; continue; }
                    if (next == 0x02) { ms.WriteByte(0x00); i++; continue; }
                    if (next == 0x03) { ms.WriteByte(0xFF); i++; continue; }
                }
                ms.WriteByte(data[i]);
            }
            return ms.ToArray();
        }

        #endregion
    }
}
