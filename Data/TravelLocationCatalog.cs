using static BusGo.Services.UiText;

namespace BusGo.Data;

public sealed record TravelLocation(string Name, string CategoryKey, string Area = "", string Aliases = "", string? SearchValue = null)
{
    public string Value => SearchValue ?? Name;
    public string Category => CategoryKey switch
    {
        "Tỉnh / thành hiện nay" => T("Tỉnh / thành hiện nay", "Current province / city"),
        "Đô thị / điểm đến" => T("Đô thị / điểm đến", "Town / destination"),
        "Bến xe tham khảo" => T("Bến xe tham khảo", "Reference bus station"),
        "Địa điểm từ tuyến BusGo" => T("Địa điểm từ tuyến BusGo", "Location from a BusGo route"),
        "Địa danh theo tên tỉnh cũ" => T("Địa danh theo tên tỉnh cũ", "Place under a former province name"),
        _ => CategoryKey
    };
    public string Detail => Area.Length == 0 ? Category : $"{Category} · {Area}";
    public string SearchText { get; } = $"{Name} {Area} {Aliases}";
}

/// <summary>Offline place suggestions, not a feed of bookable trips or confirmed pickup points.</summary>
public static class TravelLocationCatalog
{
    // Place names checked 2026-10-04. Sources and scope are recorded in README.md.
    // Keep stations distinct from their cities: suggesting a station never broadens a trip query to the city.
    private static readonly TravelLocation[] Locations = BuildLocations();
    private static readonly Dictionary<string, TravelLocation> ByName = BuildLookup(Locations);

    public static IReadOnlyList<TravelLocation> GetSuggestions(IEnumerable<string> routeLocations)
    {
        var result = new List<TravelLocation>(Locations.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coveredCatalogNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in routeLocations)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var value = name.Trim();
            if (!seen.Add(value)) continue;
            if (ByName.TryGetValue(value, out var known))
            {
                coveredCatalogNames.Add(known.Name);
                // Distinct database spellings can serve different routes; retain each original query value.
                result.Add(known.Value == value ? known : known with { Name = value, SearchValue = value });
            }
            else
            {
                result.Add(new TravelLocation(value, "Địa điểm từ tuyến BusGo"));
            }
        }
        foreach (var location in Locations)
            if (!coveredCatalogNames.Contains(location.Name)) result.Add(location);
        return result;
    }

    private static Dictionary<string, TravelLocation> BuildLookup(IEnumerable<TravelLocation> locations)
    {
        var lookup = new Dictionary<string, TravelLocation>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locations)
        {
            lookup.Add(location.Name, location);
            foreach (var alias in location.Aliases.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                lookup.TryAdd(alias, location);
        }
        return lookup;
    }

    private static TravelLocation[] BuildLocations()
    {
        const string province = "Tỉnh / thành hiện nay";
        const string destination = "Đô thị / điểm đến";
        const string station = "Bến xe tham khảo";
        List<TravelLocation> locations =
        [
            // All 34 current provincial-level units (Resolution 202/2025/QH15).
            new("Hà Nội", province, Aliases: "Hanoi|Ha Noi"),
            new("Hồ Chí Minh", province, Aliases: "TP. Hồ Chí Minh|TP Hồ Chí Minh|TPHCM|TP HCM|HCMC|Sài Gòn|Saigon"),
            new("Đà Nẵng", province, Aliases: "Danang"),
            new("Hải Phòng", province, Aliases: "Haiphong"),
            new("Cần Thơ", province, Aliases: "Cantho"),
            new("Huế", province, Aliases: "Thừa Thiên Huế|Thừa Thiên - Huế"),
            new("An Giang", province),
            new("Bắc Ninh", province),
            new("Cà Mau", province),
            new("Cao Bằng", province),
            new("Đắk Lắk", province, Aliases: "Đắc Lắc|Dak Lak"),
            new("Điện Biên", province),
            new("Đồng Nai", province),
            new("Đồng Tháp", province),
            new("Gia Lai", province),
            new("Hà Tĩnh", province),
            new("Hưng Yên", province),
            new("Khánh Hòa", province),
            new("Lai Châu", province),
            new("Lâm Đồng", province),
            new("Lạng Sơn", province),
            new("Lào Cai", province),
            new("Nghệ An", province),
            new("Ninh Bình", province),
            new("Phú Thọ", province),
            new("Quảng Ngãi", province),
            new("Quảng Ninh", province),
            new("Quảng Trị", province),
            new("Sơn La", province),
            new("Tây Ninh", province),
            new("Thái Nguyên", province),
            new("Thanh Hóa", province),
            new("Tuyên Quang", province),
            new("Vĩnh Long", province),

            // Public route destinations. Area is a geographic hint, not an administrative address.
            new("Sa Pa", destination, "Lào Cai", "Sapa"),
            new("Bắc Hà", destination, "Lào Cai"),
            new("Mù Cang Chải", destination, "Yên Bái"),
            new("Điện Biên Phủ", destination, "Điện Biên"),
            new("Mộc Châu", destination, "Sơn La"),
            new("Mai Châu", destination, "Hòa Bình"),
            new("Đồng Văn", destination, "Hà Giang"),
            new("Quản Bạ", destination, "Hà Giang"),
            new("Yên Minh", destination, "Hà Giang"),
            new("Tam Đảo", destination, "Vĩnh Phúc"),
            new("Việt Trì", destination, "Phú Thọ"),
            new("Sơn Tây", destination, "Hà Nội"),
            new("Phủ Lý", destination, "Hà Nam"),
            new("Hạ Long", destination, "Quảng Ninh", "Halong"),
            new("Móng Cái", destination, "Quảng Ninh"),
            new("Cẩm Phả", destination, "Quảng Ninh"),
            new("Tuần Châu", destination, "Hạ Long"),
            new("Cát Bà", destination, "Hải Phòng", "Đảo Cát Bà"),
            new("Tam Cốc", destination, "Ninh Bình"),
            new("Tràng An", destination, "Ninh Bình"),
            new("Sầm Sơn", destination, "Thanh Hóa"),
            new("Vinh", destination, "Nghệ An"),
            new("Cửa Lò", destination, "Nghệ An"),
            new("Phong Nha", destination, "Quảng Bình", "Động Phong Nha"),
            new("Đồng Hới", destination, "Quảng Bình"),
            new("Đông Hà", destination, "Quảng Trị"),
            new("Hội An", destination, "Quảng Nam", "Hoian|Phố cổ Hội An"),
            new("Tam Kỳ", destination, "Quảng Nam"),
            new("Núi Thành", destination, "Quảng Nam"),
            new("Quy Nhơn", destination, "Bình Định", "Quinhon"),
            new("An Nhơn", destination, "Bình Định"),
            new("Hoài Nhơn", destination, "Bình Định"),
            new("Tuy Hòa", destination, "Phú Yên"),
            new("Nha Trang", destination, "Khánh Hòa"),
            new("Cam Ranh", destination, "Khánh Hòa"),
            new("Cam Lâm", destination, "Khánh Hòa"),
            new("Ninh Hòa", destination, "Khánh Hòa"),
            new("Diên Khánh", destination, "Khánh Hòa"),
            new("Phan Rang", destination, "Ninh Thuận", "Phan Rang - Tháp Chàm|Phan Rang Tháp Chàm"),
            new("Phan Thiết", destination, "Bình Thuận"),
            new("Mũi Né", destination, "Phan Thiết"),
            new("La Gi", destination, "Bình Thuận", "Lagi"),
            new("Đà Lạt", destination, "Lâm Đồng", "Dalat"),
            new("Bảo Lộc", destination, "Lâm Đồng"),
            new("Đức Trọng", destination, "Lâm Đồng"),
            new("Di Linh", destination, "Lâm Đồng", "Thị trấn Di Linh"),
            new("Buôn Ma Thuột", destination, "Đắk Lắk", "Buôn Mê Thuột|Ban Mê Thuột|BMT"),
            new("Buôn Hồ", destination, "Đắk Lắk"),
            new("Gia Nghĩa", destination, "Đắk Nông"),
            new("Pleiku", destination, "Gia Lai", "Plei Ku"),
            new("An Khê", destination, "Gia Lai"),
            new("Biên Hòa", destination, "Đồng Nai", "Biên Hoà"),
            new("Thủ Dầu Một", destination, "Bình Dương"),
            new("Bến Cát", destination, "Bình Dương"),
            new("Đồng Xoài", destination, "Bình Phước"),
            new("Chơn Thành", destination, "Bình Phước"),
            new("Thủ Đức", destination, "Hồ Chí Minh"),
            new("Vũng Tàu", destination, "Bà Rịa - Vũng Tàu"),
            new("Bà Rịa", destination, "Bà Rịa - Vũng Tàu"),
            new("Long Hải", destination, "Bà Rịa - Vũng Tàu"),
            new("Hồ Tràm", destination, "Bà Rịa - Vũng Tàu"),
            new("Núi Bà Đen", destination, "Tây Ninh"),
            new("Gò Dầu", destination, "Tây Ninh"),
            new("Mỹ Tho", destination, "Tiền Giang"),
            new("Cai Lậy", destination, "Tiền Giang"),
            new("Cái Bè", destination, "Tiền Giang"),
            new("Cao Lãnh", destination, "Đồng Tháp"),
            new("Sa Đéc", destination, "Đồng Tháp"),
            new("Hồng Ngự", destination, "Đồng Tháp"),
            new("Thốt Nốt", destination, "Cần Thơ"),
            new("Vị Thanh", destination, "Hậu Giang"),
            new("Ngã Bảy", destination, "Hậu Giang"),
            new("Long Xuyên", destination, "An Giang"),
            new("Châu Đốc", destination, "An Giang"),
            new("Rạch Giá", destination, "Kiên Giang"),
            new("Hà Tiên", destination, "Kiên Giang"),
            new("Năm Căn", destination, "Cà Mau"),

            // Terminals are individual suggestions, never aliases for their host city.
            new("Bến xe Mỹ Đình", station, "Hà Nội", "BX Mỹ Đình|Mỹ Đình"),
            new("Bến xe Giáp Bát", station, "Hà Nội", "BX Giáp Bát|Giáp Bát"),
            new("Bến xe Nước Ngầm", station, "Hà Nội", "BX Nước Ngầm|Nước Ngầm"),
            new("Bến xe Gia Lâm", station, "Hà Nội", "BX Gia Lâm"),
            new("Bến xe Vĩnh Niệm", station, "Hải Phòng", "BX Vĩnh Niệm|Vĩnh Niệm"),
            new("Bến xe Bãi Cháy", station, "Hạ Long", "BX Bãi Cháy|Bến xe khách Bãi Cháy"),
            new("Bến xe Sa Pa", station, "Sa Pa", "BX Sapa|Bến xe Sapa"),
            new("Bến xe phía Bắc Huế", station, "Huế", "BX phía Bắc Huế"),
            new("Bến xe phía Nam Huế", station, "Huế", "BX phía Nam Huế"),
            new("Bến xe Trung tâm Đà Nẵng", station, "Đà Nẵng", "BX Đà Nẵng|Bến xe Đà Nẵng"),
            new("Bến xe Quảng Ngãi", station, "Quảng Ngãi", "BX Quảng Ngãi"),
            new("Bến xe phía Bắc Nha Trang", station, "Nha Trang", "BX phía Bắc Nha Trang"),
            new("Bến xe phía Nam Nha Trang", station, "Nha Trang", "BX phía Nam Nha Trang"),
            new("Bến xe Cam Ranh", station, "Cam Ranh", "BX Cam Ranh"),
            new("Bến xe liên tỉnh Đà Lạt", station, "Đà Lạt", "BX Đà Lạt|Bến xe Đà Lạt"),
            new("Bến xe Mũi Né", station, "Phan Thiết", "BX Mũi Né"),
            new("Bến xe Miền Đông mới", station, "Hồ Chí Minh", "BX Miền Đông mới"),
            new("Bến xe Miền Đông cũ", station, "Hồ Chí Minh", "BX Miền Đông cũ"),
            new("Bến xe Miền Tây", station, "Hồ Chí Minh", "BX Miền Tây"),
            new("Bến xe An Sương", station, "Hồ Chí Minh", "BX An Sương|An Sương"),
            new("Bến xe Ngã Tư Ga", station, "Hồ Chí Minh", "BX Ngã Tư Ga|Ngã Tư Ga"),
            new("Bến xe Vũng Tàu", station, "Vũng Tàu", "BX Vũng Tàu"),
            new("Bến xe Tây Ninh", station, "Tây Ninh", "BX Tây Ninh"),
            new("Bến xe Cần Thơ mới", station, "Cần Thơ", "BX Cần Thơ mới|Bến xe trung tâm Cần Thơ"),
            new("Bến xe Ô Môn", station, "Cần Thơ", "BX Ô Môn"),
            new("Bến xe Sóc Trăng", station, "Sóc Trăng", "BX Sóc Trăng"),
            new("Bến xe Kiên Giang", station, "Rạch Giá", "BX Kiên Giang"),
            new("Bến xe Hà Tiên", station, "Hà Tiên", "BX Hà Tiên"),
            new("Bến xe Cà Mau", station, "Cà Mau", "BX Cà Mau")
        ];

        // Retain familiar pre-merger names for travel searches; do not change legacy station/admin scope.
        var knownNames = BuildLookup(locations);
        foreach (var name in VietnamProvinces.List)
            if (!knownNames.ContainsKey(name))
                locations.Add(new TravelLocation(name, "Địa danh theo tên tỉnh cũ"));
        return locations.ToArray();
    }
}
