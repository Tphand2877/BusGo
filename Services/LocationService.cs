using BusGo.Data;
using BusGo.Models;

namespace BusGo.Services;

/// <summary>The deployment station is fixed by configuration; customer preferences are request-local.</summary>
public sealed class LocationService
{
    public const string DefaultStation = "Thái Nguyên";
    public static LocationService Instance { get; } = new();
    private readonly AsyncLocal<string?> _customerLocation = new();

    private LocationService()
    {
        var configured = Environment.GetEnvironmentVariable("STATION_LOCATION");
        IsStationPinned = !string.IsNullOrWhiteSpace(configured);
        MachineLocation = NormalizeToVietnamProvince(configured ?? DefaultStation);
    }

    public string MachineLocation { get; }
    public bool IsStationPinned { get; }
    public IReadOnlyList<string> AvailableLocations => VietnamProvinces.List;
    public string CurrentLocation
    {
        get => CurrentUser.IsAdmin ? MachineLocation : _customerLocation.Value ?? MachineLocation;
        set => SetUserLocation(value);
    }

    public bool SetUserLocation(string? location)
    {
        if (CurrentUser.IsAdmin || string.IsNullOrWhiteSpace(location)) return false;
        var normalized = NormalizeToVietnamProvince(location);
        if (string.Equals(CurrentLocation, normalized, StringComparison.OrdinalIgnoreCase)) return false;
        _customerLocation.Value = normalized;
        return true;
    }

    public static string NormalizeToVietnamProvince(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultStation;

        var input = raw.Trim();

        // Loại bỏ tiền tố "Bến xe", "Tỉnh", "Thành phố", "TP."
        var clean = input;
        foreach (var prefix in new[] { "Bến xe", "bến xe", "Tỉnh", "tỉnh", "Thành phố", "thành phố", "TP.", "TP " })
        {
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(prefix.Length).Trim();
            }
        }

        // Khớp trực tiếp với danh mục tỉnh thành Việt Nam
        var exact = VietnamProvinces.List.FirstOrDefault(p =>
            string.Equals(p, clean, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p, input, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // Khớp mờ theo không dấu hoặc ký tự chứa
        var contains = VietnamProvinces.List.FirstOrDefault(p =>
            clean.Contains(p, StringComparison.OrdinalIgnoreCase) ||
            p.Contains(clean, StringComparison.OrdinalIgnoreCase));
        if (contains != null) return contains;

        // Xử lý các tên tiếng Anh / không dấu phổ biến trả về từ IP Geolocation
        return clean.ToLowerInvariant() switch
        {
            "thai nguyen" or "thainguyen" => "Thái Nguyên",
            "hanoi" or "ha noi" => "Hà Nội",
            "ho chi minh" or "ho chi minh city" or "saigon" or "sai gon" => "TP. Hồ Chí Minh",
            "da nang" or "danang" => "Đà Nẵng",
            "hai phong" or "haiphong" => "Hải Phòng",
            "can tho" or "cantho" => "Cần Thơ",
            "quang ninh" or "ha long" => "Quảng Ninh",
            "bac kan" or "backan" => "Bắc Kạn",
            "bac ninh" or "bacninh" => "Bắc Ninh",
            "bac giang" or "bacgiang" => "Bắc Giang",
            "nam dinh" or "namdinh" => "Nam Định",
            "ninh binh" or "ninhbinh" => "Ninh Bình",
            "thanh hoa" or "thanhhoa" => "Thanh Hóa",
            "nghe an" or "vinh" => "Nghệ An",
            "ha tinh" or "hatinh" => "Hà Tĩnh",
            "lao cai" or "sapa" or "sa pa" => "Lào Cai",
            "phu tho" or "viet tri" => "Phú Thọ",
            _ => clean.Length > 0 ? clean : DefaultStation
        };
    }
}
