using BonBon.Business.Common;
using BonBon.Business.DTOs.Tracking;

namespace BonBon.Business.Services.Interfaces;

public interface ITrackingService
{
    Task<ServiceResult> SetMeetingPointsAsync(Guid accountId, Guid bookingId, SetMeetingPointsRequest request, CancellationToken ct = default);
    Task<ServiceResult> SetDeviceAsync(Guid accountId, Guid bookingId, long deviceId, CancellationToken ct = default);
    Task<ServiceResult<TrackingSnapshotResponse>> GetSnapshotAsync(Guid accountId, Guid bookingId, CancellationToken ct = default);
    Task<bool> CanAccessBookingAsync(Guid accountId, Guid bookingId, CancellationToken ct = default);
    Task<IReadOnlyList<TrackingPositionUpdate>> ProcessPositionAsync(long deviceId, decimal latitude, decimal longitude,
        decimal? accuracyMeters, DateTimeOffset recordedAt, CancellationToken ct = default);
}
