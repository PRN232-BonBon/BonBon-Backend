namespace BonBon.Business.DTOs.Tracking;

/// <summary>
/// Cho client biết tracking của mình đang ở trạng thái nào và mở lúc nào,
/// để hiển thị đếm ngược thay vì gọi snapshot liên tục.
/// </summary>
public sealed record TrackingStatusResponse(
    Guid BookingId,
    string ViewerRole,
    string Stage,
    string State,
    bool CanTrack,
    DateTimeOffset OpensAt,
    DateTimeOffset Deadline,
    int SecondsUntilOpen,
    bool MeetingPointsConfigured,
    bool TrackerConfigured);

public static class TrackingStates
{
    /// <summary>Đủ điều kiện xem trực tiếp.</summary>
    public const string Ready = "Ready";

    /// <summary>Đúng giai đoạn nhưng chưa tới 30 phút trước giờ nhận/trả xe.</summary>
    public const string WindowNotOpen = "WindowNotOpen";

    /// <summary>Chủ xe chưa thiết lập điểm nhận/trả xe.</summary>
    public const string MeetingPointsMissing = "MeetingPointsMissing";

    /// <summary>Chưa gắn thiết bị Traccar cho xe.</summary>
    public const string TrackerMissing = "TrackerMissing";

    /// <summary>Booking chưa ở giai đoạn xem được hoặc người xem không thuộc lượt thuê.</summary>
    public const string Inactive = "Inactive";
}
