namespace BonBon.Business.DTOs.Tracking;

/// <summary>
/// Vị trí GPS mới nhất của xe trong lúc thuê, kèm khoảng cách tới điểm trả xe.
/// </summary>
public sealed record TrackingSnapshotResponse(
    Guid BookingId,
    decimal Latitude,
    decimal Longitude,
    decimal? AccuracyMeters,
    DateTimeOffset RecordedAt,
    decimal DestinationLatitude,
    decimal DestinationLongitude,
    double DistanceMeters,
    bool Arrived);
