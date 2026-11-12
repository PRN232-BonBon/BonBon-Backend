using BonBon.Business.Common;
using BonBon.Business.DTOs.Tracking;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;

namespace BonBon.Business.Services.Implementations;

public sealed class TrackingService(ITrackingRepository repository) : ITrackingService
{
    private const string BookingNotFoundMessage =
        "Không tìm thấy booking hoặc bạn không thuộc lượt thuê này.";

    private const string OwnerOnlyMessage = "Chỉ chủ xe được theo dõi vị trí xe.";

    private const string PositionSource = "Phone";

    private readonly ITrackingRepository _repository = repository;

    public async Task<ServiceResult> SetMeetingPointsAsync(Guid accountId, Guid bookingId,
        SetMeetingPointsRequest request, CancellationToken ct = default)
    {
        var booking = await _repository.GetBookingAsync(bookingId, ct);
        if (booking is null || booking.OwnerAccountId != accountId)
            return ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy booking hoặc bạn không phải chủ xe.");

        if (!TrackingRules.ConfigurableStatuses.Contains(booking.Status))
            return ServiceResult.Failure("MEETING_POINTS_LOCKED",
                "Chỉ thiết lập điểm trả xe khi booking đã được xác nhận và chưa kết thúc.");

        if (TrackingRules.IsActive(booking.Status) && booking.ReturnLatitude is not null)
            return ServiceResult.Failure("MEETING_POINTS_LOCKED",
                "Tracking đã bắt đầu; điểm trả xe không thể thay đổi.");

        var updated = await _repository.SetMeetingPointsAsync(bookingId, accountId,
            request.PickupLatitude, request.PickupLongitude,
            request.ReturnLatitude, request.ReturnLongitude,
            TrackingRules.ConfigurableStatuses, TrackingRules.LiveRentalStatus, ct);
        return updated
            ? ServiceResult.Success()
            : ServiceResult.Failure("MEETING_POINTS_LOCKED",
                "Booking vừa thay đổi trạng thái, vui lòng tải lại.");
    }

    public async Task<ServiceResult<TrackingStatusResponse>> GetStatusAsync(Guid accountId, Guid bookingId,
        CancellationToken ct = default)
    {
        var booking = await _repository.GetBookingAsync(bookingId, ct);
        if (booking is null)
            return ServiceResult<TrackingStatusResponse>.Failure("BOOKING_NOT_FOUND", BookingNotFoundMessage);

        if (TrackingRules.ResolveViewerRole(accountId, booking.OwnerAccountId) is null)
            return ServiceResult<TrackingStatusResponse>.Failure("FORBIDDEN", OwnerOnlyMessage);

        var destination = TrackingRules.GetDestination(booking.ReturnLatitude, booking.ReturnLongitude);
        var state = !TrackingRules.IsActive(booking.Status) ? TrackingStates.Inactive
            : destination is null ? TrackingStates.MeetingPointsMissing
            : TrackingStates.Ready;

        return ServiceResult<TrackingStatusResponse>.Success(new TrackingStatusResponse(
            booking.BookingId,
            TrackingRules.OwnerViewer,
            state,
            state == TrackingStates.Ready,
            booking.EndAt,
            destination is not null));
    }

    public async Task<ServiceResult<TrackingSnapshotResponse>> GetSnapshotAsync(Guid accountId, Guid bookingId,
        CancellationToken ct = default)
    {
        var booking = await _repository.GetBookingAsync(bookingId, ct);
        if (booking is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("BOOKING_NOT_FOUND", BookingNotFoundMessage);

        if (TrackingRules.ResolveViewerRole(accountId, booking.OwnerAccountId) is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("FORBIDDEN", OwnerOnlyMessage);

        if (!TrackingRules.IsActive(booking.Status))
            return ServiceResult<TrackingSnapshotResponse>.Failure("TRACKING_NOT_ACTIVE",
                "Tracking chỉ mở trong thời gian thuê: bật khi chủ xe bàn giao xe (InProgress) và tắt khi khách trả xe.");

        var destination = TrackingRules.GetDestination(booking.ReturnLatitude, booking.ReturnLongitude);
        if (destination is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("MEETING_POINT_NOT_CONFIGURED",
                "Chủ xe chưa thiết lập điểm trả xe nên chưa tính được khoảng cách.");

        var position = await _repository.GetLatestPositionAsync(bookingId, ct);
        if (position is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("POSITION_NOT_AVAILABLE",
                "Chưa nhận được vị trí GPS nào từ điện thoại của người thuê.");

        var distance = TrackingRules.DistanceMeters(
            position.Latitude, position.Longitude,
            destination.Value.Latitude, destination.Value.Longitude);

        return ServiceResult<TrackingSnapshotResponse>.Success(new TrackingSnapshotResponse(
            booking.BookingId,
            position.Latitude,
            position.Longitude,
            position.AccuracyMeters,
            position.RecordedAt,
            destination.Value.Latitude,
            destination.Value.Longitude,
            distance,
            TrackingRules.IsArrived(distance)));
    }

    public async Task<bool> CanJoinTrackingAsync(Guid accountId, Guid bookingId, CancellationToken ct = default)
    {
        var booking = await _repository.GetBookingAsync(bookingId, ct);
        if (booking is null) return false;
        if (TrackingRules.ResolveViewerRole(accountId, booking.OwnerAccountId) is null) return false;

        return TrackingRules.IsActive(booking.Status)
            && TrackingRules.GetDestination(booking.ReturnLatitude, booking.ReturnLongitude) is not null;
    }

    public async Task<ServiceResult<TrackingPositionUpdate?>> ReportPositionAsync(Guid accountId,
        Guid bookingId, ReportTrackingPositionRequest request, CancellationToken ct = default)
    {
        var booking = await _repository.GetBookingAsync(bookingId, ct);
        if (booking is null)
            return ServiceResult<TrackingPositionUpdate?>.Failure("BOOKING_NOT_FOUND", BookingNotFoundMessage);

        if (accountId != booking.CustomerAccountId && accountId != booking.OwnerAccountId)
            return ServiceResult<TrackingPositionUpdate?>.Failure("FORBIDDEN",
                "Bạn không thuộc lượt thuê này nên không gửi được vị trí.");

        if (!TrackingRules.IsActive(booking.Status))
            return ServiceResult<TrackingPositionUpdate?>.Failure("TRACKING_NOT_ACTIVE",
                "Chỉ gửi vị trí khi chuyến đang diễn ra (InProgress).");

        var now = DateTimeOffset.UtcNow;
        var recordedAt = request.RecordedAt is { } value && value < now ? value : now;

        await _repository.SavePositionAsync(bookingId, request.Latitude, request.Longitude,
            request.AccuracyMeters, recordedAt, PositionSource, ct);

        var destination = TrackingRules.GetDestination(booking.ReturnLatitude, booking.ReturnLongitude);
        if (destination is null)
            return ServiceResult<TrackingPositionUpdate?>.Success(null);

        var distance = TrackingRules.DistanceMeters(
            request.Latitude, request.Longitude,
            destination.Value.Latitude, destination.Value.Longitude);

        return ServiceResult<TrackingPositionUpdate?>.Success(new TrackingPositionUpdate(
            booking.BookingId,
            request.Latitude,
            request.Longitude,
            request.AccuracyMeters,
            recordedAt,
            destination.Value.Latitude,
            destination.Value.Longitude,
            distance,
            TrackingRules.IsArrived(distance)));
    }
}
