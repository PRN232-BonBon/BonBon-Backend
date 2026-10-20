using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Tracking;

public sealed class SetMeetingPointsRequest
{
    [Range(typeof(decimal), "-90", "90")]
    public required decimal PickupLatitude { get; init; }
    [Range(typeof(decimal), "-180", "180")]
    public required decimal PickupLongitude { get; init; }
    [Range(typeof(decimal), "-90", "90")]
    public required decimal ReturnLatitude { get; init; }
    [Range(typeof(decimal), "-180", "180")]
    public required decimal ReturnLongitude { get; init; }
}

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
