using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using DayZLauncher.Helpers;


namespace DayZLauncher.Models
{
    public class Server : ObservableModel
    {
        private string _name = "Unknown Server";
        private string _map = string.Empty;
        private bool _favorite;
        private int _currentPlayers;
        private int _maxPlayers;
        private int _ping = -1;
        private bool _isOnline;
        private bool _isExpanded;
        private bool _isQuerying;
        private string _gameVersion = string.Empty;
        private string? _passwordEncrypted;
        private List<string> _requiredMods = new();


        public string Id { get; set; } = string.Empty;       // ip:port or a unique key


        private string _battleMetricsId = string.Empty;


        /// <summary>
        /// BattleMetrics' own numeric server id. Required by the player-count-history endpoint;
        /// empty for a Direct Connect target that BattleMetrics has never seen. Notifies so the
        /// mods chip can appear the moment an expand-backfill identifies the server.
        /// </summary>
        public string BattleMetricsId
        {
            get => _battleMetricsId;
            set
            {
                if (SetProperty(ref _battleMetricsId, value))
                {
                    OnPropertyChanged(nameof(ModsKnown));
                }
            }
        }


        /// <summary>
        /// Whether BattleMetrics flags this as a Bohemia official server (details.official).
        /// Authoritative - the launcher previously guessed from the server name, which never
        /// matched Bohemia's actual "0867 | EUROPE - DE" naming.
        /// </summary>
        public bool IsOfficial { get; set; }


        private string _country = string.Empty;


        /// <summary>Two-letter country code, resolved from the server's IP by the feed
        /// backend (public-domain dataset - no lookups from user machines).</summary>
        public string Country
        {
            get => _country;
            set
            {
                if (SetProperty(ref _country, value))
                {
                    OnPropertyChanged(nameof(RegionDisplay));
                }
            }
        }


        /// <summary>Continent name for display/filtering, or "Unknown" when the IP has not
        /// been resolved by any source.</summary>
        [JsonIgnore]
        public string RegionDisplay
        {
            get
            {
                string continent = Helpers.Continents.OfCountry(_country);
                return continent.Length == 0 ? "Unknown" : $"{continent} ({_country.ToUpperInvariant()})";
            }
        }


        /// <summary>BattleMetrics popularity rank; lower is more popular.</summary>
        public int Rank { get; set; }


        /// <summary>Whether the server requires a password (from BattleMetrics details).</summary>
        public bool RequiresPassword { get; set; }


        private bool _isFirstPersonOnly;
        private bool _perspectiveKnown;


        /// <summary>True when the server runs first-person only (it advertises the "no3rd"
        /// tag). Only meaningful once <see cref="PerspectiveKnown"/> is set.</summary>
        public bool IsFirstPersonOnly
        {
            get => _isFirstPersonOnly;
            set
            {
                if (SetProperty(ref _isFirstPersonOnly, value))
                {
                    OnPropertyChanged(nameof(PerspectiveDisplay));
                }
            }
        }


        /// <summary>
        /// True once ANY real source has stated this server's perspective: the feed's "fpp"
        /// field, the Steam master list's gametags, or the server's own query answer. Without
        /// it the absence of "no3rd" means "unknown", not "third person allowed" - the 1PP/3PP
        /// filters only trust rows whose perspective was actually reported.
        /// </summary>
        public bool PerspectiveKnown
        {
            get => _perspectiveKnown;
            set
            {
                if (SetProperty(ref _perspectiveKnown, value))
                {
                    OnPropertyChanged(nameof(PerspectiveDisplay));
                }
            }
        }


        /// <summary>Camera perspective for the detail drawer.</summary>
        [JsonIgnore]
        public string PerspectiveDisplay =>
            !_perspectiveKnown ? "Unknown" : _isFirstPersonOnly ? "1PP only" : "1PP & 3PP";


        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }


        public string Ip { get; set; } = string.Empty;


        /// <summary>The game port players connect on (the -port= launch argument).</summary>
        public int Port { get; set; } = 2302;


        /// <summary>
        /// The Steam A2S query port, which is NOT the same as the game port. Queries used to be
        /// sent to <see cref="Port"/>, which nothing answers on, so every live refresh timed out
        /// and reported servers as offline with no ping. BattleMetrics reports the real value as
        /// "portQuery"; when it is missing, DayZ's convention is game port + 1.
        /// </summary>
        public int QueryPort { get; set; }


        [JsonIgnore]
        public int EffectiveQueryPort => QueryPort > 0 ? QueryPort : Port + 1;


        /// <summary>
        /// Raw map identifier exactly as the server reports it (e.g. "chernarusplus", "enoch").
        /// Use <see cref="MapDisplay"/> for anything user-facing.
        /// </summary>
        public string Map
        {
            get => _map;
            set
            {
                if (SetProperty(ref _map, value))
                {
                    OnPropertyChanged(nameof(MapDisplay));
                }
            }
        }


        /// <summary>Friendly map name ("chernarusplus" -> "Chernarus"), or "Unknown" if unreported.</summary>
        [JsonIgnore]
        public string MapDisplay => MapNames.ToDisplayName(_map);


        public List<string> RequiredMods
        {
            get => _requiredMods;
            set
            {
                if (SetProperty(ref _requiredMods, value ?? new List<string>()))
                {
                    OnPropertyChanged(nameof(ModCount));
                    OnPropertyChanged(nameof(IsModded));
                    OnPropertyChanged(nameof(ModsKnown));
                }
            }
        }


        private bool _modsVerified;


        /// <summary>
        /// True once a direct A2S rules query has returned this server's actual mod list -
        /// including an empty one, which is how a vanilla server is *proven* vanilla rather
        /// than merely lacking data.
        /// </summary>
        [JsonIgnore]
        public bool ModsVerified
        {
            get => _modsVerified;
            set
            {
                if (SetProperty(ref _modsVerified, value))
                {
                    OnPropertyChanged(nameof(ModsKnown));
                }
            }
        }


        /// <summary>
        /// Whether the mod list can be trusted at all. Steam's master list carries no mod data,
        /// so a Steam-sourced row would otherwise render a confident-looking "0 Mods" chip that
        /// is simply a lie - the card hides the chip until one of the real sources has spoken
        /// (BattleMetrics knows the server, or the server itself answered a rules query).
        /// </summary>
        [JsonIgnore]
        public bool ModsKnown => _requiredMods.Count > 0 || _modsVerified || !string.IsNullOrEmpty(BattleMetricsId);


        /// <summary>
        /// Mod display names from BattleMetrics, positionally aligned with <see cref="RequiredMods"/>.
        /// Supplied free by the API, so the required-mod list can show real titles without a
        /// Steam lookup per mod.
        /// </summary>
        public List<string> ModNames { get; set; } = new();


        /// <summary>
        /// The required mods as display rows: id, resolved name, and cached preview image.
        /// Rebuilt whenever the ids or names change.
        /// </summary>
        [JsonIgnore]
        public ObservableCollection<ServerModEntry> ModEntries { get; } = new();


        [JsonIgnore]
        public int ModCount => _requiredMods.Count;


        [JsonIgnore]
        public bool IsModded => _requiredMods.Count > 0;


        public bool Favorite
        {
            get => _favorite;
            set => SetProperty(ref _favorite, value);
        }


        public DateTime? LastPlayedAt { get; set; }


        /// <summary>
        /// When a Steam master-list enumeration last contained this server. Drives cache
        /// expiry: a server is only pruned after it has been absent for days, because any
        /// single enumeration misses a few hundred live servers whose pings dropped.
        /// </summary>
        public DateTime? LastSeenAt { get; set; }


        public string? PasswordEncrypted
        {
            get => _passwordEncrypted;
            set
            {
                if (SetProperty(ref _passwordEncrypted, value))
                {
                    OnPropertyChanged(nameof(HasPassword));
                }
            }
        }


        // Live fields retrieved via A2S query or BattleMetrics
        public int CurrentPlayers
        {
            get => _currentPlayers;
            set
            {
                if (SetProperty(ref _currentPlayers, value))
                {
                    OnPropertyChanged(nameof(PlayerDisplay));
                }
            }
        }


        public int MaxPlayers
        {
            get => _maxPlayers;
            set
            {
                if (SetProperty(ref _maxPlayers, value))
                {
                    OnPropertyChanged(nameof(PlayerDisplay));
                }
            }
        }


        private int _queueSize;


        /// <summary>
        /// Players waiting to log in (DayZ publishes this as "lqsN" in its query keywords).
        /// Shown as a "+N" badge next to the player count on full servers.
        /// </summary>
        [JsonIgnore]
        public int QueueSize
        {
            get => _queueSize;
            set
            {
                if (SetProperty(ref _queueSize, value))
                {
                    OnPropertyChanged(nameof(QueueDisplay));
                    OnPropertyChanged(nameof(HasQueue));
                }
            }
        }


        [JsonIgnore]
        public string QueueDisplay => _queueSize > 0 ? $"+{_queueSize}" : string.Empty;


        [JsonIgnore]
        public bool HasQueue => _queueSize > 0 && IsOnline;


        /// <summary>Round-trip time in ms, or -1 when the server has not been pinged yet.</summary>
        public int Ping
        {
            get => _ping;
            set
            {
                if (SetProperty(ref _ping, value))
                {
                    OnPropertyChanged(nameof(PingDisplay));
                }
            }
        }


        public bool IsOnline
        {
            get => _isOnline;
            set
            {
                if (SetProperty(ref _isOnline, value))
                {
                    OnPropertyChanged(nameof(PlayerDisplay));
                    OnPropertyChanged(nameof(PingDisplay));
                }
            }
        }


        /// <summary>Whether this card is showing its inline detail drawer in the server list.</summary>
        [JsonIgnore]
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }


        /// <summary>
        /// True for rows whose live numbers came from Steam's master-list heartbeat rather than
        /// a source that vouched for them. Fake "255/255" spam listings live here, so these rows
        /// are demoted to offline when a direct query gets no answer - unlike BattleMetrics rows,
        /// where a lost UDP exchange must never override the API's word.
        /// </summary>
        [JsonIgnore]
        public bool SteamSourced { get; set; }


        /// <summary>
        /// When a direct query last verified (or failed to verify) this row. Page visits
        /// re-verify anything staler than a minute, so flipping between pages always shows
        /// current populations and pings without hammering rows that were just checked.
        /// </summary>
        [JsonIgnore]
        public DateTime? LastVerifiedAt { get; set; }


        /// <summary>
        /// True once this server has ever backed a claimed population with an actual player
        /// list. Such a row is exempt from every group-level spam verdict - it has proven
        /// it's real, regardless of what its neighbours or lookalikes are.
        /// </summary>
        [JsonIgnore]
        public bool PopVerified { get; set; }


        /// <summary>
        /// Set when THIS machine's own query confirmed the server unreachable; cleared only
        /// when a local query succeeds. Deliberately untouched by feed/stream merges - "the
        /// feed says it's online" must never resurrect a row the user's machine proved dead
        /// (that exact resurrection was how fakes reappeared after every Refresh).
        /// </summary>
        [JsonIgnore]
        public bool LocallyDead { get; set; }


        /// <summary>Set when this row twice ANSWERED the player-list query with nobody while
        /// claiming a crowd - the adaptive-spam signature (real populated servers answer with
        /// entries; privacy-minded hosts don't answer at all).</summary>
        [JsonIgnore]
        public bool PopLied { get; set; }


        /// <summary>True while a live A2S query for this server is in flight.</summary>
        [JsonIgnore]
        public bool IsQuerying
        {
            get => _isQuerying;
            set => SetProperty(ref _isQuerying, value);
        }


        /// <summary>
        /// Whether <see cref="IsOnline"/> was established by a direct A2S query rather than by
        /// the server-list API.
        ///
        /// Plenty of DayZ hosts firewall their Steam query port, and the query port itself is a
        /// guess whenever the API omits it. A failed UDP probe therefore is not proof the server
        /// is down, and must not be allowed to overwrite a player count the API just reported -
        /// doing so turned whole pages of live servers into a wall of "Offline".
        /// </summary>
        [JsonIgnore]
        public bool OnlineFromQuery { get; set; }


        public string GameVersion
        {
            get => _gameVersion;
            set
            {
                if (SetProperty(ref _gameVersion, value))
                {
                    OnPropertyChanged(nameof(GameVersionDisplay));
                }
            }
        }


        /// <summary>
        /// Version for display. TargetNullValue in the XAML doesn't cover this, because an
        /// unqueried server has an *empty* GameVersion rather than a null one, which rendered as
        /// a blank space next to the "Version:" label.
        /// </summary>
        [JsonIgnore]
        public string GameVersionDisplay => string.IsNullOrWhiteSpace(GameVersion) ? "Unknown" : GameVersion;


        [JsonIgnore]
        public bool HasPassword => !string.IsNullOrEmpty(PasswordEncrypted);


        // Computed properties
        [JsonIgnore]
        public string PlayerDisplay => IsOnline ? $"{CurrentPlayers}/{MaxPlayers}" : "Offline";


        [JsonIgnore]
        public string PingDisplay => Ping >= 0 ? $"{Ping} ms" : "---";


        [JsonIgnore]
        public string Address => $"{Ip}:{Port}";
    }
}




