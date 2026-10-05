namespace BusGo.Models;

public sealed record TripSearchPanelModel(
    IReadOnlyList<string> Origins,
    IReadOnlyList<string> Destinations,
    string? Origin = null,
    string? Destination = null,
    DateTime? Date = null);
