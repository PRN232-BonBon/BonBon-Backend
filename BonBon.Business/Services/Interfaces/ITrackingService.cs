using BonBon.Business.Common;
using BonBon.Business.DTOs.Tracking;

namespace BonBon.Business.Services.Interfaces;

public interface ITrackingService
{
    Task<ServiceResult> SetMeetingPointsAsync(Guid accountId, Guid bookingId,
        SetMeetingPointsRequest request, CancellationToken ct = default);

    Task<ServiceResult<TrackingStatusResponse>> GetStatusAsync(Guid accountId, Guid bookingId,
        CancellationToken ct = default);

    Task<ServiceResult<TrackingSnapshotResponse>> GetSnapshotAsync(Guid accountId, Guid bookingId,
        CancellationToken ct = default);

    /// <summary>Hub chỉ cho chủ xe vào nhóm khi tracking đang mở và đã có điểm trả xe.</summary>
    Task<bool> CanJoinTrackingAsync(Guid accountId, Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// Lưu vị trí GPS do điện thoại của người thuê gửi lên. Trả về bản cập nhật để
    /// phát trực tiếp cho chủ xe, hoặc null khi chưa có điểm trả xe để tính khoảng cách.
    /// </summary>
    Task<ServiceResult<TrackingPositionUpdate?>> ReportPositionAsync(Guid accountId, Guid bookingId,
        ReportTrackingPositionRequest request, CancellationToken ct = default);
}
