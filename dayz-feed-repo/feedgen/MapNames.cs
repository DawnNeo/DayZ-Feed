using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace DayZLauncher.Helpers
{
    /// <summary>
    /// The DayZ terrain catalogue: every map the launcher knows about, and the raw world
    /// identifiers servers actually report for it.
    ///
    /// Servers report the world's folder/class name ("chernarusplus", "enoch", "sakhal"),
    /// never the marketing name. The browser used to compare those raw values against a
    /// hardcoded list of five pretty names, so the map filter matched nothing at all.
    ///
    /// BattleMetrics' DayZ payload carries no map field whatsoever - verified against the live
    /// API - so a map is only ever learned from a direct A2S query. A server that has not
    /// answered a query yet legitimately has an unknown map.
    ///
    /// Compiled from three independent sources (dayz.xam.nu, iZurvive, and DayZ server-hosting
    /// mpmissions naming). No world identifier is shared between two maps, so a wrong entry can
    /// only cause a map to display title-cased - it cannot mislabel a different terrain.
    /// </summary>
    public static class MapNames
    {
        public const string Unknown = "Unknown";

        /// <summary>Raw world identifier -> friendly display name.</summary>
        private static readonly Dictionary<string, string> WorldToDisplay = new(StringComparer.OrdinalIgnoreCase)
        {
            ["acadia"] = "Acadia",
            ["altalake"] = "Alta Lake",
            ["alteria"] = "Alteria",
            ["anastara"] = "Anastara",
            ["andromeda_map"] = "Andromeda",
            ["antoria"] = "Antoria",
            ["arkfall"] = "Arkfall",
            ["arland"] = "Arland",
            ["arsteinen"] = "Arsteinen",
            ["arsteinen_snow"] = "Arsteinen",
            ["ashesandblood_map"] = "Ashes and Blood",
            ["avalon"] = "Avalon",
            ["azalea"] = "Azalea",
            ["banov"] = "Banov",
            ["banovfrost"] = "Banov Frost",
            ["banovplus"] = "Banov Plus",
            ["barrington"] = "Barrington",
            ["bearisland"] = "Bear Island",
            ["bitterroot"] = "Bitterroot",
            ["broumovsko"] = "Broumovsko",
            ["burnham"] = "Burnham",
            ["burnhammeurtre"] = "Burnham",
            ["capare"] = "Capare",
            ["chernarusplus"] = "Chernarus",
            ["chernarus"] = "Chernarus",
            ["chernarusplusexp"] = "Chernarus",
            ["dayzoffline.chernarusplus"] = "Chernarus",
            ["chernarus2035"] = "Chernarus 2035",
            ["chernarusplusgloom"] = "Chernarus Gloom",
            ["chiemsee"] = "Chiemsee",
            ["deadcity"] = "Dead City",
            ["deadfall"] = "Deadfall",
            ["deerisle"] = "Deer Isle",
            ["doorcounty"] = "Door County",
            ["echo_09e"] = "Echo 09E",
            ["fenix_emptiness"] = "Emptiness (Fenix)",
            ["esseker"] = "Esseker",
            ["eternal"] = "Eternal",
            ["evelone2666"] = "Evelone 2666",
            ["evelone"] = "Evelone 2666",
            ["exclusionzone"] = "Exclusion Zone",
            ["exclusionzoneplus"] = "Exclusion Zone Plus",
            ["fallujah"] = "Fallujah",
            ["flatpack"] = "Flatpack",
            ["florianopolis"] = "Florianopolis",
            ["fogfall"] = "Fog Fall",
            ["greencounty"] = "Green County",
            ["hashima"] = "Hashima Islands",
            ["hashimaislands"] = "Hashima Islands",
            ["hom"] = "Heart of Moscow (Metro)",
            ["metrohom"] = "Heart of Moscow (Metro)",
            ["heavenshollow"] = "Heaven's Hollow",
            ["iceshaft"] = "Iceshaft",
            ["isolationzone"] = "Isolation Zone",
            ["iztek"] = "Iztek",
            ["kuba"] = "Kuba",
            ["lazona"] = "La Zona",
            ["lakecity"] = "Lake City",
            ["enoch"] = "Livonia",
            ["enochexp"] = "Livonia",
            ["dayzoffline.enoch"] = "Livonia",
            ["enochgloom"] = "Livonia Gloom",
            ["lux"] = "Lux / Lux Redux",
            ["luxredux"] = "Lux / Lux Redux",
            ["magadan"] = "Magadan",
            ["dlmalden"] = "Malden",
            ["malvinas82"] = "Malvinas 82",
            ["manhattan"] = "Manhattan",
            ["mecklenburg"] = "Mecklenburg",
            ["medieval"] = "Medieval DayZ / Dark Medieval Age",
            ["darkmedievalage"] = "Medieval DayZ / Dark Medieval Age",
            ["melkart"] = "Melkart",
            ["melkart_v2"] = "Melkart",
            ["menskisland"] = "Mensk Island",
            ["mohonk"] = "Mohonk",
            ["muerta"] = "Muerta Islands",
            ["muertaislands"] = "Muerta Islands",
            ["mysteryisland"] = "Mystery Island",
            ["nachtigallmap"] = "Nachtigall",
            ["namalsk"] = "Namalsk",
            ["namalskredux"] = "Namalsk Redux",
            ["napfz"] = "Napf",
            ["newisland"] = "New Island",
            ["newyork"] = "New York",
            ["nhchernobyl"] = "NH Chernobyl (NewHaven / New Horizon Chernobyl Zone)",
            ["newhavenchernobyl"] = "NH Chernobyl (NewHaven / New Horizon Chernobyl Zone)",
            ["newhaven"] = "NH Chernobyl (NewHaven / New Horizon Chernobyl Zone)",
            ["northtakistan"] = "North Takistan",
            ["novikostok"] = "Novikostok",
            ["novomundo"] = "Novo Mundo",
            ["nukezzone"] = "NukeZZone",
            ["nyheim"] = "Nyheim",
            ["badg_nyheim"] = "Nyheim",
            ["onforin"] = "Onforin",
            ["ontonogan"] = "Ontonogan",
            ["orsek"] = "Orsek",
            ["pnw"] = "PNW (Pacific Northwest)",
            ["pripyat"] = "Pripyat",
            ["pripyatgamma"] = "Pripyat",
            ["prisonisland"] = "Prison Island",
            ["prominsk"] = "Prominsk",
            ["queenstown_newzealand"] = "Queenstown New Zealand",
            ["raman"] = "Raman",
            ["riodejaneiro"] = "Rio de Janeiro",
            ["rio"] = "Rio de Janeiro",
            ["rio_map"] = "Rio de Janeiro",
            ["ros"] = "ROS",
            ["rostow"] = "Rostow",
            ["sahinkaya"] = "Sahinkaya",
            ["sahrani"] = "Sahrani",
            ["sakhal"] = "Sakhal",
            ["dayzoffline.sakhal"] = "Sakhal",
            ["sanfranciscobayarea"] = "San Francisco Bay Area",
            ["sangudo"] = "Sangudo",
            ["santabarbara"] = "Santa Barbara Island",
            ["santabarbaraisland"] = "Santa Barbara Island",
            ["sarov"] = "Sarov",
            ["saxonya"] = "Saxonya",
            ["scalasaig"] = "Scalasaig",
            ["scilly_island"] = "Scilly Island",
            ["scillyisland"] = "Scilly Island",
            ["scorpionisland"] = "Scorpion Island",
            ["scotland"] = "Scotland DayZ",
            ["shauwaki_islands"] = "Shauwaki Islands",
            ["siberia"] = "Siberia",
            ["stuartisland"] = "Stuart Island",
            ["stuart_island"] = "Stuart Island",
            ["swansisland"] = "Swan's Island",
            ["takistan"] = "Takistan",
            ["takistanplus"] = "Takistan Plus",
            ["taviana"] = "Taviana",
            ["channel"] = "The Channel",
            ["thezone"] = "The Zone (S.T.A.L.K.E.R.)",
            ["togenia"] = "Togenia",
            ["tombstone"] = "Tombstone",
            ["utes"] = "Utes",
            ["utes_2022"] = "Utes",
            ["valning"] = "Valning",
            ["valskisland"] = "Valsk Island",
            ["varlind"] = "Varlind",
            ["vela"] = "Vela",
            ["visisland"] = "Vis Island",
            ["vis_island"] = "Vis Island",
            ["vis"] = "Vis Island",
            ["wildisland"] = "Wild Island",
            ["xzone"] = "XZone Chernobyl",
            ["chernobylzone"] = "XZone Chernobyl",
            ["yiprit"] = "Yiprit",
            ["zagoria"] = "Zagoria",
            ["zaha"] = "Zaha",
            ["zaruun"] = "Zaruun",
            ["zelador"] = "Zelador",
        };

        /// <summary>Every known map name, alphabetical. Drives the browser's map filter.</summary>
        private static readonly string[] AllMapNames =
        {
            "Acadia",
            "Alta Lake",
            "Alteria",
            "Anastara",
            "Andromeda",
            "Antoria",
            "Arkfall",
            "Arland",
            "Arsteinen",
            "Ashes and Blood",
            "Avalon",
            "Azalea",
            "Banov",
            "Banov Frost",
            "Banov Plus",
            "Barrington",
            "Bear Island",
            "Bitterroot",
            "Broumovsko",
            "Burnham",
            "Capare",
            "Chernarus",
            "Chernarus 2035",
            "Chernarus Gloom",
            "Chiemsee",
            "Dead City",
            "Deadfall",
            "Deer Isle",
            "Door County",
            "Echo 09E",
            "Emptiness (Fenix)",
            "Esseker",
            "Eternal",
            "Evelone 2666",
            "Exclusion Zone",
            "Exclusion Zone Plus",
            "Fallujah",
            "Flatpack",
            "Florianopolis",
            "Fog Fall",
            "Green County",
            "Hashima Islands",
            "Heart of Moscow (Metro)",
            "Heaven's Hollow",
            "Iceshaft",
            "Isolation Zone",
            "Iztek",
            "Kuba",
            "La Zona",
            "Lake City",
            "Livonia",
            "Livonia Gloom",
            "Lux / Lux Redux",
            "Magadan",
            "Malden",
            "Malvinas 82",
            "Manhattan",
            "Mecklenburg",
            "Medieval DayZ / Dark Medieval Age",
            "Melkart",
            "Mensk Island",
            "Mohonk",
            "Muerta Islands",
            "Mystery Island",
            "Nachtigall",
            "Namalsk",
            "Namalsk Redux",
            "Napf",
            "New Island",
            "New York",
            "NH Chernobyl (NewHaven / New Horizon Chernobyl Zone)",
            "North Takistan",
            "Novikostok",
            "Novo Mundo",
            "NukeZZone",
            "Nyheim",
            "Onforin",
            "Ontonogan",
            "Orsek",
            "PNW (Pacific Northwest)",
            "Pripyat",
            "Prison Island",
            "Prominsk",
            "Queenstown New Zealand",
            "Raman",
            "Rio de Janeiro",
            "ROS",
            "Rostow",
            "Sahinkaya",
            "Sahrani",
            "Sakhal",
            "San Francisco Bay Area",
            "Sangudo",
            "Santa Barbara Island",
            "Sarov",
            "Saxonya",
            "Scalasaig",
            "Scilly Island",
            "Scorpion Island",
            "Scotland DayZ",
            "Shauwaki Islands",
            "Siberia",
            "Stuart Island",
            "Swan's Island",
            "Takistan",
            "Takistan Plus",
            "Taviana",
            "The Channel",
            "The Zone (S.T.A.L.K.E.R.)",
            "Togenia",
            "Tombstone",
            "Utes",
            "Valning",
            "Valsk Island",
            "Varlind",
            "Vela",
            "Vis Island",
            "Wild Island",
            "XZone Chernobyl",
            "Yiprit",
            "Zagoria",
            "Zaha",
            "Zaruun",
            "Zelador",
        };

        /// <summary>Bohemia first-party terrains.</summary>
        public static readonly IReadOnlyList<string> OfficialMaps = new[]
        {
            "Chernarus",
            "Livonia",
            "Sakhal",
        };

        /// <summary>
        /// Maps whose world identifier came from a single source and could not be corroborated.
        /// Still offered in the filter; a mismatch here only means the map shows title-cased.
        /// </summary>
        public static readonly IReadOnlyList<string> LowConfidenceMaps = new[]
        {
            "Acadia",
            "Alta Lake",
            "Andromeda",
            "Arkfall",
            "Ashes and Blood",
            "Banov Plus",
            "Broumovsko",
            "Burnham",
            "Dead City",
            "Echo 09E",
            "Emptiness (Fenix)",
            "Evelone 2666",
            "Fallujah",
            "Flatpack",
            "Florianopolis",
            "Heart of Moscow (Metro)",
            "Heaven's Hollow",
            "Iceshaft",
            "Kuba",
            "La Zona",
            "Lake City",
            "Magadan",
            "Malden",
            "Malvinas 82",
            "Manhattan",
            "Medieval DayZ / Dark Medieval Age",
            "Mensk Island",
            "Mohonk",
            "Muerta Islands",
            "Mystery Island",
            "Nachtigall",
            "Namalsk Redux",
            "Napf",
            "Novo Mundo",
            "Ontonogan",
            "Orsek",
            "Prison Island",
            "Prominsk",
            "Queenstown New Zealand",
            "Sahinkaya",
            "Sangudo",
            "Santa Barbara Island",
            "Saxonya",
            "Scalasaig",
            "Scilly Island",
            "Scorpion Island",
            "Scotland DayZ",
            "Shauwaki Islands",
            "The Channel",
            "Tombstone",
            "Valsk Island",
            "Varlind",
            "Wild Island",
            "XZone Chernobyl",
            "Zagoria",
            "Zaruun",
        };

        public static IReadOnlyList<string> AllKnownMaps => AllMapNames;

        /// <summary>
        /// Converts a raw map identifier to a display name. Unrecognised worlds are title-cased
        /// rather than discarded, so a brand new community terrain still shows its real name
        /// instead of being mislabelled as a stock map.
        /// </summary>
        public static string ToDisplayName(string? rawMap)
        {
            if (string.IsNullOrWhiteSpace(rawMap)) return Unknown;

            string trimmed = rawMap.Trim();
            if (WorldToDisplay.TryGetValue(trimmed, out var friendly)) return friendly;

            // Some servers report the full mission folder, e.g. "dayzOffline.chernarusplus".
            int dot = trimmed.LastIndexOf('.');
            if (dot >= 0 && dot < trimmed.Length - 1)
            {
                string tail = trimmed.Substring(dot + 1);
                if (WorldToDisplay.TryGetValue(tail, out var byTail)) return byTail;
                trimmed = tail;
            }

            string cleaned = trimmed.Replace('_', ' ').Replace('-', ' ');
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleaned.ToLowerInvariant());
        }

        /// <summary>
        /// Builds the map filter list: the supplied 'all' entry, then every known map, with any
        /// unrecognised map seen on live servers appended so nothing on screen is unfilterable.
        /// </summary>
        public static ObservableCollection<string> BuildFilterList(string allEntry, IEnumerable<string> seenMapNames)
        {
            var list = new ObservableCollection<string> { allEntry };
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { allEntry };

            // Maps with servers currently on screen come first - the full 124-map catalogue is
            // below them, but leading with populated ones means the top of the dropdown always
            // produces results.
            foreach (var name in seenMapNames.Where(n => !string.IsNullOrWhiteSpace(n) && n != Unknown)
                                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (added.Add(name)) list.Add(name);
            }

            foreach (var name in AllMapNames)
            {
                if (added.Add(name)) list.Add(name);
            }

            return list;
        }

        /// <summary>
        /// The short, searchable form of a display name - "PNW (Pacific Northwest)" -> "PNW",
        /// "Lux / Lux Redux" -> "Lux". Used when a map pick falls back to a name search on
        /// BattleMetrics, whose search matches server names.
        /// </summary>
        public static string ToSearchTerm(string displayName)
        {
            string term = displayName;
            int cut = term.IndexOf(" (", StringComparison.Ordinal);
            if (cut > 0) term = term[..cut];
            cut = term.IndexOf(" /", StringComparison.Ordinal);
            if (cut > 0) term = term[..cut];
            return term.Trim();
        }
    }
}
