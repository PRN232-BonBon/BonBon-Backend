using BonBon.Business.Common;
using BonBon.Business.DTOs.Tracking;

namespace BonBon.Business.Services.Interfaces;

public interface ITrackingService
{
    Task<ServiceResult> SetMeetingPointsAsync(Guid accountId, Guid bookingId,
        SetMeetingPointsRequest request, CancellationToken ct = default);

    Task<ServiceResult> SetDeviceAsync(Guid accountId, Guid bookingId, long deviceId,
        CancellationToken ct = default);

    Task<ServiceResult<TrackingStatusResponse>> GetStatusAsync(Guid accountId, Guid bookingId,
        CancellationToken ct = default);

    Task<ServiceResult<TrackingSnapshotResponse>> GetSnapshotAsync(Guid accountId, Guid bookingId,
        CancellationToken ct = default);

    /// <summary>Hub chỉ cho vào nhóm khi đúng vai trò, đủ cấu hình và tracking đang mở.</summary>
    Task<bool> CanJoinTrackingAsync(Guid accountId, Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// Lưu vị trí GPS và trả về các cập nhật được phép phát trực tiếp.
    /// Vị trí vẫn được lưu ngoài khung 30 phút, nhưng chỉ phát khi tracking mở.
    /// </summary>
    Task<IReadOnlyList<TrackingPositionUpdate>> ProcessPositionAsync(long deviceId, decimal latitude,
        decimal longitude, decimal? accuracyMeters, DateTimeOffset recordedAt, CancellationToken ct = default);
}
