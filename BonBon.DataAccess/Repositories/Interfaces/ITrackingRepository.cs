namespace BonBon.DataAccess.Repositories.Interfaces;

/// <summary>Dữ liệu booking cần cho quyết định tracking và snapshot.</summary>
public sealed record TrackingBooking(
    Guid BookingId,
    Guid CustomerAccountId,
    Guid OwnerAccountId,
    string Status,
    DateTimeOffset EndAt,
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
    /// Chỉ chủ xe cập nhật được. Chỉ đổi được khi booking chưa bắt đầu thuê, hoặc
    /// khi đang thuê mà điểm trả xe còn thiếu (lần thiết lập đầu tiên).
    /// Trạng thái hợp lệ do <c>TrackingRules</c> quyết định và được truyền vào.
    /// </summary>
    Task<bool> SetMeetingPointsAsync(Guid bookingId, Guid ownerAccountId, decimal pickupLatitude,
        decimal pickupLongitude, decimal returnLatitude, decimal returnLongitude,
        IReadOnlyList<string> allowedStatuses, string activeStatus, CancellationToken ct = default);

    /// <summary>Lưu một fix GPS do điện thoại gửi lên.</summary>
    Task SavePositionAsync(Guid bookingId, decimal latitude, decimal longitude, decimal? accuracyMeters,
        DateTimeOffset recordedAt, string source, CancellationToken ct = default);

    Task<StoredVehiclePosition?> GetLatestPositionAsync(Guid bookingId, CancellationToken ct = default);
}
