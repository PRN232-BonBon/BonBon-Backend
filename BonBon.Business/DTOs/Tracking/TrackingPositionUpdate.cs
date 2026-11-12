namespace BonBon.Business.DTOs.Tracking;

/// <summary>Cập nhật trực tiếp phát qua SignalR cho chủ xe của booking.</summary>
public sealed record TrackingPositionUpdate(
    Guid BookingId,
    decimal Latitude,
    decimal Longitude,
    decimal? AccuracyMeters,
    DateTimeOffset RecordedAt,
    decimal DestinationLatitude,
    decimal DestinationLongitude,
    double DistanceMeters,
    bool Arrived);
