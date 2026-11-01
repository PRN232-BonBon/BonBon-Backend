using BonBon.Business.Common;
using BonBon.Business.DTOs.Tracking;
using BonBon.Business.Services.Interfaces;
using BonBon.Business.Tracking;
using BonBon.DataAccess.Repositories.Interfaces;

namespace BonBon.Business.Services.Implementations;

public sealed class TrackingService(ITrackingRepository repository) : ITrackingService
{
    private const string BookingNotFoundMessage =
        "Không tìm thấy booking hoặc bạn không thuộc lượt thuê này.";

    private readonly ITrackingRepository _repository = repository;

    public async Task<ServiceResult> SetMeetingPointsAsync(Guid accountId, Guid bookingId,
        SetMeetingPointsRequest request, CancellationToken ct = default)
    {
        var booking = await _repository.GetBookingAsync(bookingId, ct);
        if (booking is null || booking.OwnerAccountId != accountId)
            return ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy booking hoặc bạn không phải chủ xe.");

        if (booking.Status is not ("Confirmed" or "ReadyForPickup"))
            return ServiceResult.Failure("MEETING_POINTS_LOCKED",
                "Chỉ thiết lập điểm nhận/trả xe khi booking đã được xác nhận và chuyến chưa bắt đầu.");

        var windowOpened = TrackingRules.IsWindowOpen(
            TrackingRules.PickupStage, booking.StartAt, booking.EndAt, DateTimeOffset.UtcNow);
        var alreadyConfigured = booking.PickupLatitude is not null && booking.ReturnLatitude is not null;
        if (windowOpened && alreadyConfigured)
            return ServiceResult.Failure("MEETING_POINTS_LOCKED",
                "Tracking đã bắt đầu; điểm nhận/trả xe phải được thiết lập trước đó và không thể thay đổi.");

        var updated = await _repository.SetMeetingPointsAsync(bookingId, accountId,
            request.PickupLatitude, request.PickupLongitude,
            request.ReturnLatitude, request.ReturnLongitude, ct);
        return updated
            ? ServiceResult.Success()
            : ServiceResult.Failure("MEETING_POINTS_LOCKED",
                "Booking vừa thay đổi trạng thái hoặc tracking đã mở, vui lòng tải lại.");
    }

    public async Task<ServiceResult> SetDeviceAsync(Guid accountId, Guid bookingId, long deviceId,
        CancellationToken ct = default)
    {
        if (deviceId <= 0)
            return ServiceResult.Failure("INVALID_DEVICE_ID", "deviceId phải lớn hơn 0.");

        var booking = await _repository.GetBookingAsync(bookingId, ct);
        if (booking is null || booking.OwnerAccountId != accountId)
            return ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy booking hoặc bạn không phải chủ xe.");

        var updated = await _repository.SetDeviceAsync(bookingId, accountId, deviceId, ct);
        return updated
            ? ServiceResult.Success()
            : ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy booking.");
    }

    public async Task<ServiceResult<TrackingStatusResponse>> GetStatusAsync(Guid accountId, Guid bookingId,
        CancellationToken ct = default)
    {
        var viewer = await ResolveViewerAsync(accountId, bookingId, ct);
        if (viewer is null)
            return ServiceResult<TrackingStatusResponse>.Failure("BOOKING_NOT_FOUND", BookingNotFoundMessage);

        var booking = viewer.Value.Booking;
        var stage = viewer.Value.Stage;
        var now = DateTimeOffset.UtcNow;
        var deadline = TrackingRules.GetRentalBoundary(stage, booking.StartAt, booking.EndAt);
        var opensAt = TrackingRules.GetWindowOpensAt(stage, booking.StartAt, booking.EndAt);
        var destination = TrackingRules.GetDestination(stage,
            booking.PickupLatitude, booking.PickupLongitude,
            booking.ReturnLatitude, booking.ReturnLongitude);
        var meetingPointsConfigured = destination is not null;
        var trackerConfigured = booking.DeviceId is not null;
        var windowOpen = now >= opensAt;

        var state = !TrackingRules.SupportsStage(booking.Status, stage) ? TrackingStates.Inactive
            : !meetingPointsConfigured ? TrackingStates.MeetingPointsMissing
            : !trackerConfigured ? TrackingStates.TrackerMissing
            : !windowOpen ? TrackingStates.WindowNotOpen
            : TrackingStates.Ready;

        var secondsUntilOpen = windowOpen
            ? 0
            : (int)Math.Ceiling((opensAt - now).TotalSeconds);

        return ServiceResult<TrackingStatusResponse>.Success(new TrackingStatusResponse(
            booking.BookingId,
            viewer.Value.Role,
            stage,
            state,
            state == TrackingStates.Ready,
            opensAt,
            deadline,
            secondsUntilOpen,
            meetingPointsConfigured,
            trackerConfigured));
    }

    public async Task<ServiceResult<TrackingSnapshotResponse>> GetSnapshotAsync(Guid accountId, Guid bookingId,
        CancellationToken ct = default)
    {
        var viewer = await ResolveViewerAsync(accountId, bookingId, ct);
        if (viewer is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("BOOKING_NOT_FOUND", BookingNotFoundMessage);

        var booking = viewer.Value.Booking;
        var stage = viewer.Value.Stage;
        if (!TrackingRules.SupportsStage(booking.Status, stage))
            return ServiceResult<TrackingSnapshotResponse>.Failure("TRACKING_NOT_ACTIVE",
                "Chưa tới giai đoạn theo dõi của bạn: người thuê xem xe tới điểm nhận khi booking Confirmed/ReadyForPickup, chủ xe xem xe về điểm trả khi booking InProgress.");

        var destination = TrackingRules.GetDestination(stage,
            booking.PickupLatitude, booking.PickupLongitude,
            booking.ReturnLatitude, booking.ReturnLongitude);
        if (destination is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("MEETING_POINT_NOT_CONFIGURED",
                "Chủ xe chưa thiết lập điểm nhận/trả xe. Điểm hẹn phải được thiết lập trước khi tracking mở.");

        if (booking.DeviceId is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("TRACKER_NOT_CONFIGURED",
                "Xe chưa được gắn thiết bị Traccar.");

        var opensAt = TrackingRules.GetWindowOpensAt(stage, booking.StartAt, booking.EndAt);
        if (DateTimeOffset.UtcNow < opensAt)
            return ServiceResult<TrackingSnapshotResponse>.Failure("TRACKING_WINDOW_NOT_OPEN",
                $"Chỉ xem trực tiếp từ {TrackingRules.FormatVietnamTime(opensAt)} " +
                $"({TrackingRules.LeadTimeMinutes} phút trước {TrackingRules.GetBoundaryLabel(stage)}).");

        var position = await _repository.GetLatestPositionAsync(bookingId, ct);
        if (position is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("POSITION_NOT_AVAILABLE",
                "Traccar chưa nhận được vị trí GPS mới.");

        var distance = TrackingRules.DistanceMeters(
            position.Latitude, position.Longitude,
            destination.Value.Latitude, destination.Value.Longitude);

        return ServiceResult<TrackingSnapshotResponse>.Success(new TrackingSnapshotResponse(
            booking.BookingId,
            stage,
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
        var viewer = await ResolveViewerAsync(accountId, bookingId, ct);
        if (viewer is null) return false;

        var booking = viewer.Value.Booking;
        var stage = viewer.Value.Stage;
        var destination = TrackingRules.GetDestination(stage,
            booking.PickupLatitude, booking.PickupLongitude,
            booking.ReturnLatitude, booking.ReturnLongitude);

        return TrackingRules.SupportsStage(booking.Status, stage)
            && destination is not null
            && booking.DeviceId is not null
            && TrackingRules.IsWindowOpen(stage, booking.StartAt, booking.EndAt, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<TrackingPositionUpdate>> ProcessPositionAsync(long deviceId, decimal latitude,
        decimal longitude, decimal? accuracyMeters, DateTimeOffset recordedAt, CancellationToken ct = default)
    {
        var targets = await _repository.GetTargetsForDeviceAsync(deviceId, ct);
        var now = DateTimeOffset.UtcNow;
        var updates = new List<TrackingPositionUpdate>(targets.Count);

        foreach (var target in targets)
        {
            await _repository.SavePositionAsync(target.BookingId, latitude, longitude, accuracyMeters, recordedAt, ct);

            var stage = TrackingRules.GetCandidateStage(target.Status);
            if (stage is null
                || !TrackingRules.IsWindowOpen(stage, target.StartAt, target.EndAt, now)) continue;

            var destination = TrackingRules.GetDestination(stage,
                target.PickupLatitude, target.PickupLongitude,
                target.ReturnLatitude, target.ReturnLongitude);
            if (destination is null) continue;

            var distance = TrackingRules.DistanceMeters(
                latitude, longitude,
                destination.Value.Latitude, destination.Value.Longitude);

            updates.Add(new TrackingPositionUpdate(
                target.BookingId,
                stage,
                latitude,
                longitude,
                accuracyMeters,
                recordedAt,
                destination.Value.Latitude,
                destination.Value.Longitude,
                distance,
                TrackingRules.IsArrived(distance)));
        }

        return updates;
    }

    private async Task<TrackingViewer?> ResolveViewerAsync(Guid accountId, Guid bookingId, CancellationToken ct)
    {
        var booking = await _repository.GetBookingAsync(bookingId, ct);
        if (booking is null) return null;

        var role = TrackingRules.ResolveViewerRole(
            accountId, booking.CustomerAccountId, booking.OwnerAccountId);
        if (role is null) return null;

        return new TrackingViewer(booking, role, TrackingRules.GetViewerStage(role));
    }

    private readonly record struct TrackingViewer(TrackingBooking Booking, string Role, string Stage);
}
