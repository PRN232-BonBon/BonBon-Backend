namespace BonBon.Business.DTOs.Tracking;

/// <summary>Cập nhật trực tiếp phát qua SignalR tới nhóm của booking.</summary>
public sealed record TrackingPositionUpdate(
    Guid BookingId,
    string Stage,
    decimal Latitude,
    decimal Longitude,
    decimal? AccuracyMeters,
    DateTimeOffset RecordedAt,
    decimal DestinationLatitude,
    decimal DestinationLongitude,
    double DistanceMeters,
    bool Arrived);
