namespace BonBon.DataAccess.Repositories.Interfaces;

/// <summary>Dữ liệu booking cần cho quyết định tracking và snapshot.</summary>
public sealed record TrackingBooking(
    Guid BookingId,
    Guid CustomerAccountId,
    Guid OwnerAccountId,
    long? DeviceId,
    string Status,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    decimal? PickupLatitude,
    decimal? PickupLongitude,
    decimal? ReturnLatitude,
    decimal? ReturnLongitude);

/// <summary>Booking đang gắn với một thiết bị Traccar đang hoạt động.</summary>
public sealed record TrackedBookingTarget(
    Guid BookingId,
    string Status,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    decimal? PickupLatitude,
    decimal? PickupLongitude,
    decimal? ReturnLatitude,
    decimal? ReturnLongitude);

public sealed record StoredVehiclePosition(
    decimal Latitude,
    decimal Longitude,
    decimal? AccuracyMeters,
    DateTimeOffset RecordedAt);

public interface ITrackingRepository
{
    Task<TrackingBooking?> GetBookingAsync(Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// Chỉ chủ xe cập nhật được. Điểm hẹn phải thiết lập trước khi tracking mở;
    /// sau khi mở thì khoá, trừ lần thiết lập đầu tiên còn thiếu.
    /// </summary>
    Task<bool> SetMeetingPointsAsync(Guid bookingId, Guid ownerAccountId, decimal pickupLatitude,
        decimal pickupLongitude, decimal returnLatitude, decimal returnLongitude, CancellationToken ct = default);

    Task<bool> SetDeviceAsync(Guid bookingId, Guid ownerAccountId, long deviceId, CancellationToken ct = default);

    /// <summary>Booking đang chạy hoặc sắp tới giờ hẹn của thiết bị này.</summary>
    Task<IReadOnlyList<TrackedBookingTarget>> GetTargetsForDeviceAsync(long deviceId, CancellationToken ct = default);

    Task SavePositionAsync(Guid bookingId, decimal latitude, decimal longitude, decimal? accuracyMeters,
        DateTimeOffset recordedAt, CancellationToken ct = default);

    Task<StoredVehiclePosition?> GetLatestPositionAsync(Guid bookingId, CancellationToken ct = default);
}
