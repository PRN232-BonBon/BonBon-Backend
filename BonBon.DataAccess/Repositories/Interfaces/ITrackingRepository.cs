namespace BonBon.DataAccess.Repositories.Interfaces;

public sealed record TrackingBooking(
    Guid BookingId,
    Guid CustomerAccountId,
    Guid OwnerAccountId,
    long? DeviceId,
    string Status,
    decimal? PickupLatitude,
    decimal? PickupLongitude,
    decimal? ReturnLatitude,
    decimal? ReturnLongitude);

public sealed record TrackedBookingTarget(
    Guid BookingId,
    Guid CustomerAccountId,
    Guid OwnerAccountId,
    string Status,
    decimal DestinationLatitude,
    decimal DestinationLongitude);

public sealed record StoredVehiclePosition(
    decimal Latitude,
    decimal Longitude,
    decimal? AccuracyMeters,
    DateTimeOffset RecordedAt);

public interface ITrackingRepository
{
    Task<TrackingBooking?> GetBookingAsync(Guid bookingId, CancellationToken ct = default);
    Task<bool> SetMeetingPointsAsync(Guid bookingId, Guid ownerAccountId, decimal pickupLatitude,
        decimal pickupLongitude, decimal returnLatitude, decimal returnLongitude, CancellationToken ct = default);
    Task<bool> SetDeviceAsync(Guid bookingId, Guid ownerAccountId, long deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<TrackedBookingTarget>> GetTargetsForDeviceAsync(long deviceId, CancellationToken ct = default);
    Task SavePositionAsync(Guid bookingId, decimal latitude, decimal longitude, decimal? accuracyMeters,
        DateTimeOffset recordedAt, CancellationToken ct = default);
    Task<StoredVehiclePosition?> GetLatestPositionAsync(Guid bookingId, CancellationToken ct = default);
}
