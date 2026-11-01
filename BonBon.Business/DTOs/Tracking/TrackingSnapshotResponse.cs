namespace BonBon.Business.DTOs.Tracking;

/// <summary>
/// Vị trí GPS mới nhất của xe so với điểm hẹn của người đang xem.
/// Chỉ trả được khi tracking đang trong 30 phút trước giờ nhận/trả xe.
/// </summary>
public sealed record TrackingSnapshotResponse(
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
