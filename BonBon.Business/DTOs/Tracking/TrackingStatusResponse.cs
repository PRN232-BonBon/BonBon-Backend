namespace BonBon.Business.DTOs.Tracking;

/// <summary>
/// Trạng thái tracking của booking đối với chủ xe. Tracking tự bật khi booking
/// chuyển sang "đang thuê" và tự tắt khi khách trả xe.
/// </summary>
public sealed record TrackingStatusResponse(
    Guid BookingId,
    string ViewerRole,
    string State,
    bool CanTrack,
    DateTimeOffset Deadline,
    bool MeetingPointsConfigured);

public static class TrackingStates
{
    /// <summary>Đang thuê và đã có điểm trả xe: xem trực tiếp được.</summary>
    public const string Ready = "Ready";

    /// <summary>Chủ xe chưa thiết lập điểm trả xe nên không tính được khoảng cách.</summary>
    public const string MeetingPointsMissing = "MeetingPointsMissing";

    /// <summary>Booking chưa bàn giao xe hoặc đã trả xe.</summary>
    public const string Inactive = "Inactive";
}
