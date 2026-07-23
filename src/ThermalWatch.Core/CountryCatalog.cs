using System.Collections.Frozen;
using System.Globalization;

namespace ThermalWatch.Core;

public static class CountryCatalog
{
    private const string IsoAlpha3Codes =
        "ABW AFG AGO AIA ALA ALB AND ARE ARG ARM ASM ATA ATF ATG AUS AUT AZE BDI BEL BEN BES BFA BGD BGR BHR BHS BIH BLM BLR BLZ BMU BOL BRA BRB BRN BTN BVT BWA CAF CAN CCK CHE CHL CHN CIV CMR COD COG COK COL COM CPV CRI CUB CUW CXR CYM CYP CZE DEU DJI DMA DNK DOM DZA ECU EGY ERI ESH ESP EST ETH FIN FJI FLK FRA FRO FSM GAB GBR GEO GGY GHA GIB GIN GLP GMB GNB GNQ GRC GRD GRL GTM GUF GUM GUY HKG HMD HND HRV HTI HUN IDN IMN IND IOT IRL IRN IRQ ISL ISR ITA JAM JEY JOR JPN KAZ KEN KGZ KHM KIR KNA KOR KWT LAO LBN LBR LBY LCA LIE LKA LSO LTU LUX LVA MAC MAF MAR MCO MDA MDG MDV MEX MHL MKD MLI MLT MMR MNE MNG MNP MOZ MRT MSR MTQ MUS MWI MYS MYT NAM NCL NER NFK NGA NIC NIU NLD NOR NPL NRU NZL OMN PAK PAN PCN PER PHL PLW PNG POL PRI PRK PRT PRY PSE PYF QAT REU ROU RUS RWA SAU SDN SEN SGP SGS SHN SJM SLB SLE SLV SMR SOM SPM SRB SSD STP SUR SVK SVN SWE SWZ SXM SYC SYR TCA TCD TGO THA TJK TKL TKM TLS TON TTO TUN TUR TUV TWN TZA UGA UKR UMI URY USA UZB VAT VCT VEN VGB VIR VNM VUT WLF WSM YEM ZAF ZMB ZWE";

    private static readonly FrozenSet<string> s_codes = IsoAlpha3Codes
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly Lazy<FrozenDictionary<string, string>> s_displayNames = new(CreateDisplayNames);

    public static bool IsValid(string code) => s_codes.Contains(code);

    public static string GetDisplayName(string code) =>
        s_displayNames.Value.GetValueOrDefault(code, code);

    private static FrozenDictionary<string, string> CreateDisplayNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (CultureInfo culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                names.TryAdd(region.ThreeLetterISORegionName.ToUpperInvariant(), region.EnglishName);
            }
            catch (ArgumentException)
            {
                // Some synthetic cultures do not identify a geographical region.
            }
        }

        return names.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
