namespace ServiceNowDesk.Client;

/// <summary>
/// Arup region and office names shown on Settings. Office labels are stored as written.
/// Group labels are headings only and are not saved as offices.
/// </summary>
public static class RegionOfficeCatalog
{
    public static IReadOnlyList<RegionOfficeGroup> Regions { get; } =
    [
        Group("Americas", offices:
        [
            "Boston",
            "Toronto",
            "Montreal",
            "Chicago",
            "Los Angeles",
            "New York",
            "New Jersey (Newark)",
            "San Francisco",
            "Oakland",
            "Bogotá",
            "Seattle",
            "Dallas",
            "Houston",
            "Washington DC",
            "San Diego"
        ]),
        Group("APAC (Asia Pacific)", groups:
        [
            Group("Australia & New Zealand", offices:
            [
                "Adelaide",
                "Brisbane",
                "Cairns",
                "Canberra",
                "Melbourne",
                "Perth",
                "Sydney",
                "Townsville",
                "Auckland"
            ]),
            Group("Southeast Asia", offices:
            [
                "Singapore",
                "Jakarta",
                "Kuala Lumpur / Penang (Malaysia)"
            ]),
            Group("East Asia", offices:
            [
                "Hong Kong",
                "Macau",
                "Beijing",
                "Shanghai",
                "Chongqing",
                "Wuhan",
                "Shenzhen",
                "Guangzhou",
                "Tokyo",
                "Seoul",
                "Taipei"
            ]),
            Group("Other APAC Offices", offices:
            [
                "Manila",
                "Bangkok",
                "Ho Chi Minh City",
                "Phnom Penh"
            ])
        ]),
        Group("Europe", groups:
        [
            Group("Denmark", offices: ["Copenhagen"]),
            Group("Germany", offices: ["Berlin", "Düsseldorf", "Frankfurt"]),
            Group("Ireland", offices: ["Dublin"]),
            Group("Netherlands", offices: ["Amsterdam", "Utrecht"]),
            Group("Poland / Southeast Europe", offices: ["Warsaw", "Kraków", "Belgrade"]),
            Group("Southern Europe", offices: ["Madrid", "Milan"]),
            Group("Türkiye", offices: ["Istanbul", "Ankara"])
        ]),
        Group("UKIMEA (UK, India, Middle East & Africa)", groups:
        [
            Group("United Kingdom", offices:
            [
                "Belfast",
                "Bristol",
                "Cardiff",
                "Edinburgh",
                "Glasgow",
                "Leeds",
                "Liverpool",
                "London",
                "Manchester",
                "Newcastle",
                "Nottingham",
                "Sheffield",
                "Solihull / Birmingham",
                "Stockton",
                "Whitehaven",
                "Winchester",
                "York"
            ]),
            Group("India", offices: ["Bengaluru", "Delhi", "Hyderabad", "Mumbai"]),
            Group("Middle East", offices: ["Abu Dhabi", "Dubai", "Doha"]),
            Group("Africa", offices:
            [
                "Cape Town",
                "Durban",
                "Johannesburg",
                "Mauritius (Bagatelle)",
                "Lagos",
                "Abuja",
                "Nairobi",
                "Harare"
            ])
        ])
    ];

    public static IReadOnlyList<string> OfficeNames { get; } = Flatten(Regions);

    public static IReadOnlyList<string> OfficesUnder(RegionOfficeGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var names = new List<string>();
        WalkOffices(group, names);
        return names;
    }

    private static void WalkOffices(RegionOfficeGroup group, List<string> names)
    {
        names.AddRange(group.Offices);
        foreach (var child in group.Groups)
            WalkOffices(child, names);
    }

    private static IReadOnlyList<string> Flatten(IReadOnlyList<RegionOfficeGroup> regions)
    {
        var names = new List<string>();
        foreach (var region in regions)
            WalkOffices(region, names);
        return names;
    }

    private static RegionOfficeGroup Group(
        string label,
        IReadOnlyList<string>? offices = null,
        IReadOnlyList<RegionOfficeGroup>? groups = null) =>
        new(label, groups ?? [], offices ?? []);
}

public sealed record RegionOfficeGroup(
    string Label,
    IReadOnlyList<RegionOfficeGroup> Groups,
    IReadOnlyList<string> Offices);
