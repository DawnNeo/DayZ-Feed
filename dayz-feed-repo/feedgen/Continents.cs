using System;
using System.Collections.Generic;


namespace DayZLauncher.Helpers
{
    /// <summary>
    /// ISO 3166-1 alpha-2 country code -> continent name, for the region filter. The feed
    /// backend publishes only the two-letter code (a dozen bytes per server); the mapping to
    /// the six filterable continents lives here so both stay tiny and keyless.
    /// </summary>
    public static class Continents
    {
        public const string Europe = "Europe";
        public const string NorthAmerica = "North America";
        public const string SouthAmerica = "South America";
        public const string Asia = "Asia";
        public const string Oceania = "Oceania";
        public const string Africa = "Africa";


        /// <summary>The filterable continents, in the order the region dropdown lists them.</summary>
        public static readonly string[] All = { Europe, NorthAmerica, SouthAmerica, Asia, Oceania, Africa };


        /// <summary>Continent for a two-letter country code, or "" when unknown/empty.</summary>
        public static string OfCountry(string? cc)
        {
            if (string.IsNullOrWhiteSpace(cc)) return string.Empty;
            return Map.TryGetValue(cc.Trim(), out var continent) ? continent : string.Empty;
        }


        private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            // Europe
            ["AD"] = Europe, ["AL"] = Europe, ["AT"] = Europe, ["AX"] = Europe, ["BA"] = Europe,
            ["BE"] = Europe, ["BG"] = Europe, ["BY"] = Europe, ["CH"] = Europe, ["CY"] = Europe,
            ["CZ"] = Europe, ["DE"] = Europe, ["DK"] = Europe, ["EE"] = Europe, ["ES"] = Europe,
            ["FI"] = Europe, ["FO"] = Europe, ["FR"] = Europe, ["GB"] = Europe, ["GG"] = Europe,
            ["GI"] = Europe, ["GR"] = Europe, ["HR"] = Europe, ["HU"] = Europe, ["IE"] = Europe,
            ["IM"] = Europe, ["IS"] = Europe, ["IT"] = Europe, ["JE"] = Europe, ["LI"] = Europe,
            ["LT"] = Europe, ["LU"] = Europe, ["LV"] = Europe, ["MC"] = Europe, ["MD"] = Europe,
            ["ME"] = Europe, ["MK"] = Europe, ["MT"] = Europe, ["NL"] = Europe, ["NO"] = Europe,
            ["PL"] = Europe, ["PT"] = Europe, ["RO"] = Europe, ["RS"] = Europe, ["RU"] = Europe,
            ["SE"] = Europe, ["SI"] = Europe, ["SJ"] = Europe, ["SK"] = Europe, ["SM"] = Europe,
            ["UA"] = Europe, ["VA"] = Europe, ["XK"] = Europe,


            // North America (incl. Central America and the Caribbean)
            ["AG"] = NorthAmerica, ["AI"] = NorthAmerica, ["AW"] = NorthAmerica, ["BB"] = NorthAmerica,
            ["BL"] = NorthAmerica, ["BM"] = NorthAmerica, ["BQ"] = NorthAmerica, ["BS"] = NorthAmerica,
            ["BZ"] = NorthAmerica, ["CA"] = NorthAmerica, ["CR"] = NorthAmerica, ["CU"] = NorthAmerica,
            ["CW"] = NorthAmerica, ["DM"] = NorthAmerica, ["DO"] = NorthAmerica, ["GD"] = NorthAmerica,
            ["GL"] = NorthAmerica, ["GP"] = NorthAmerica, ["GT"] = NorthAmerica, ["HN"] = NorthAmerica,
            ["HT"] = NorthAmerica, ["JM"] = NorthAmerica, ["KN"] = NorthAmerica, ["KY"] = NorthAmerica,
            ["LC"] = NorthAmerica, ["MF"] = NorthAmerica, ["MQ"] = NorthAmerica, ["MS"] = NorthAmerica,
            ["MX"] = NorthAmerica, ["NI"] = NorthAmerica, ["PA"] = NorthAmerica, ["PM"] = NorthAmerica,
            ["PR"] = NorthAmerica, ["SV"] = NorthAmerica, ["SX"] = NorthAmerica, ["TC"] = NorthAmerica,
            ["TT"] = NorthAmerica, ["US"] = NorthAmerica, ["VC"] = NorthAmerica, ["VG"] = NorthAmerica,
            ["VI"] = NorthAmerica,


            // South America
            ["AR"] = SouthAmerica, ["BO"] = SouthAmerica, ["BR"] = SouthAmerica, ["CL"] = SouthAmerica,
            ["CO"] = SouthAmerica, ["EC"] = SouthAmerica, ["FK"] = SouthAmerica, ["GF"] = SouthAmerica,
            ["GY"] = SouthAmerica, ["PE"] = SouthAmerica, ["PY"] = SouthAmerica, ["SR"] = SouthAmerica,
            ["UY"] = SouthAmerica, ["VE"] = SouthAmerica,


            // Asia (incl. the Middle East; TR spans both but hosts in Asia's direction)
            ["AE"] = Asia, ["AF"] = Asia, ["AM"] = Asia, ["AZ"] = Asia, ["BD"] = Asia,
            ["BH"] = Asia, ["BN"] = Asia, ["BT"] = Asia, ["CN"] = Asia, ["GE"] = Asia,
            ["HK"] = Asia, ["ID"] = Asia, ["IL"] = Asia, ["IN"] = Asia, ["IQ"] = Asia,
            ["IR"] = Asia, ["JO"] = Asia, ["JP"] = Asia, ["KG"] = Asia, ["KH"] = Asia,
            ["KP"] = Asia, ["KR"] = Asia, ["KW"] = Asia, ["KZ"] = Asia, ["LA"] = Asia,
            ["LB"] = Asia, ["LK"] = Asia, ["MM"] = Asia, ["MN"] = Asia, ["MO"] = Asia,
            ["MV"] = Asia, ["MY"] = Asia, ["NP"] = Asia, ["OM"] = Asia, ["PH"] = Asia,
            ["PK"] = Asia, ["PS"] = Asia, ["QA"] = Asia, ["SA"] = Asia, ["SG"] = Asia,
            ["SY"] = Asia, ["TH"] = Asia, ["TJ"] = Asia, ["TL"] = Asia, ["TM"] = Asia,
            ["TR"] = Asia, ["TW"] = Asia, ["UZ"] = Asia, ["VN"] = Asia, ["YE"] = Asia,


            // Oceania
            ["AS"] = Oceania, ["AU"] = Oceania, ["CK"] = Oceania, ["FJ"] = Oceania,
            ["FM"] = Oceania, ["GU"] = Oceania, ["KI"] = Oceania, ["MH"] = Oceania,
            ["MP"] = Oceania, ["NC"] = Oceania, ["NF"] = Oceania, ["NR"] = Oceania,
            ["NU"] = Oceania, ["NZ"] = Oceania, ["PF"] = Oceania, ["PG"] = Oceania,
            ["PW"] = Oceania, ["SB"] = Oceania, ["TK"] = Oceania, ["TO"] = Oceania,
            ["TV"] = Oceania, ["VU"] = Oceania, ["WF"] = Oceania, ["WS"] = Oceania,


            // Africa
            ["AO"] = Africa, ["BF"] = Africa, ["BI"] = Africa, ["BJ"] = Africa, ["BW"] = Africa,
            ["CD"] = Africa, ["CF"] = Africa, ["CG"] = Africa, ["CI"] = Africa, ["CM"] = Africa,
            ["CV"] = Africa, ["DJ"] = Africa, ["DZ"] = Africa, ["EG"] = Africa, ["EH"] = Africa,
            ["ER"] = Africa, ["ET"] = Africa, ["GA"] = Africa, ["GH"] = Africa, ["GM"] = Africa,
            ["GN"] = Africa, ["GQ"] = Africa, ["GW"] = Africa, ["KE"] = Africa, ["KM"] = Africa,
            ["LR"] = Africa, ["LS"] = Africa, ["LY"] = Africa, ["MA"] = Africa, ["MG"] = Africa,
            ["ML"] = Africa, ["MR"] = Africa, ["MU"] = Africa, ["MW"] = Africa, ["MZ"] = Africa,
            ["NA"] = Africa, ["NE"] = Africa, ["NG"] = Africa, ["RE"] = Africa, ["RW"] = Africa,
            ["SC"] = Africa, ["SD"] = Africa, ["SL"] = Africa, ["SN"] = Africa, ["SO"] = Africa,
            ["SS"] = Africa, ["ST"] = Africa, ["SZ"] = Africa, ["TD"] = Africa, ["TG"] = Africa,
            ["TN"] = Africa, ["TZ"] = Africa, ["UG"] = Africa, ["YT"] = Africa, ["ZA"] = Africa,
            ["ZM"] = Africa, ["ZW"] = Africa,
        };
    }
}




