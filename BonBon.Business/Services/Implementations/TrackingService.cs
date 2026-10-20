using BonBon.Business.Common;
using BonBon.Business.DTOs.Tracking;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;

namespace BonBon.Business.Services.Implementations;

public sealed class TrackingService(ITrackingRepository repository) : ITrackingService
{
    private readonly ITrackingRepository _repository = repository;

    public async Task<ServiceResult> SetMeetingPointsAsync(Guid accountId, Guid bookingId,
        SetMeetingPointsRequest request, CancellationToken ct = default)
    {
        var booking = await _repository.GetBookingAsync(bookingId, ct);
        if (booking is null || booking.OwnerAccountId != accountId)
            return ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy booking hoặc bạn không phải chủ xe.");
        if (booking.Status is not ("Confirmed" or "ReadyForPickup"))
            return ServiceResult.Failure("MEETING_POINTS_LOCKED", "Chỉ được thiết lập điểm hẹn khi booking đang Confirmed hoặc ReadyForPickup.");

        var updated = await _repository.SetMeetingPointsAsync(bookingId, accountId,
            request.PickupLatitude, request.PickupLongitude,
            request.ReturnLatitude, request.ReturnLongitude, ct);
        return updated ? ServiceResult.Success()
            : ServiceResult.Failure("MEETING_POINTS_LOCKED", "Booking vừa thay đổi trạng thái, vui lòng tải lại.");
    }

    public async Task<ServiceResult> SetDeviceAsync(Guid accountId, Guid bookingId, long deviceId, CancellationToken ct = default)
    {
        if (deviceId <= 0) return ServiceResult.Failure("INVALID_DEVICE_ID", "deviceId phải lớn hơn 0.");
        var booking = await _repository.GetBookingAsync(bookingId, ct);
        if (booking is null || booking.OwnerAccountId != accountId)
            return ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy booking hoặc bạn không phải chủ xe.");
        var updated = await _repository.SetDeviceAsync(bookingId, accountId, deviceId, ct);
        return updated ? ServiceResult.Success() : ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy booking.");
    }

    public async Task<ServiceResult<TrackingSnapshotResponse>> GetSnapshotAsync(Guid accountId, Guid bookingId, CancellationToken ct = default)
    {
        var booking = await _repository.GetBookingAsync(bookingId, ct);
        if (booking is null || (booking.CustomerAccountId != accountId && booking.OwnerAccountId != accountId))
            return ServiceResult<TrackingSnapshotResponse>.Failure("BOOKING_NOT_FOUND", "Không tìm thấy booking.");

        var stage = GetStage(booking, accountId);
        if (stage is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("TRACKING_NOT_ACTIVE", "Chưa tới giai đoạn theo dõi xe cho tài khoản này.");
        if (booking.DeviceId is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("TRACKER_NOT_CONFIGURED", "Xe chưa được liên kết thiết bị Traccar.");

        var destination = GetDestination(booking, stage);
        if (destination is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("MEETING_POINT_NOT_CONFIGURED", "Chủ xe chưa thiết lập tọa độ điểm hẹn.");
        var position = await _repository.GetLatestPositionAsync(bookingId, ct);
        if (position is null)
            return ServiceResult<TrackingSnapshotResponse>.Failure("POSITION_NOT_AVAILABLE", "Traccar chưa nhận được vị trí GPS mới.");

        var distance = DistanceMeters(position.Latitude, position.Longitude, destination.Value.Latitude, destination.Value.Longitude);
        return ServiceResult<TrackingSnapshotResponse>.Success(new TrackingSnapshotResponse(
            bookingId, stage, position.Latitude, position.Longitude, position.AccuracyMeters,
            position.RecordedAt, destination.Value.Latitude, destination.Value.Longitude,
            distance, IsArrived(distance)));
    }

    public async Task<bool> CanAccessBookingAsync(Guid accountId, Guid bookingId, CancellationToken ct = default)
    {
        var booking = await _repository.GetBookingAsync(bookingId, ct);
        return booking is not null && (booking.CustomerAccountId == accountId || booking.OwnerAccountId == accountId);
    }

    public async Task<IReadOnlyList<TrackingPositionUpdate>> ProcessPositionAsync(long deviceId, decimal latitude,
        decimal longitude, decimal? accuracyMeters, DateTimeOffset recordedAt, CancellationToken ct = default)
    {
        var targets = await _repository.GetTargetsForDeviceAsync(deviceId, ct);
        var updates = new List<TrackingPositionUpdate>(targets.Count);
        foreach (var target in targets)
        {
            await _repository.SavePositionAsync(target.BookingId, latitude, longitude, accuracyMeters, recordedAt, ct);
            var stage = target.Status == "ReadyForPickup" ? "pickup" : "return";
            var distance = DistanceMeters(latitude, longitude, target.DestinationLatitude, target.DestinationLongitude);
            updates.Add(new TrackingPositionUpdate(target.BookingId, stage, latitude, longitude, accuracyMeters,
                recordedAt, target.DestinationLatitude, target.DestinationLongitude, distance,
                IsArrived(distance)));
        }
        return updates;
    }

    private static string? GetStage(TrackingBooking booking, Guid accountId) => booking.Status switch
    {
        "ReadyForPickup" when booking.CustomerAccountId == accountId => "pickup",
        "InProgress" when booking.OwnerAccountId == accountId => "return",
        _ => null,
    };

    private static (decimal Latitude, decimal Longitude)? GetDestination(TrackingBooking booking, string stage)
    {
        var latitude = stage == "pickup" ? booking.PickupLatitude : booking.ReturnLatitude;
        var longitude = stage == "pickup" ? booking.PickupLongitude : booking.ReturnLongitude;
        return latitude is not null && longitude is not null ? (latitude.Value, longitude.Value) : null;
    }

    private static double DistanceMeters(decimal lat1, decimal lon1, decimal lat2, decimal lon2)
    {
        const double earthRadiusMeters = 6_371_000;
        var dLat = DegreesToRadians((double)(lat2 - lat1));
        var dLon = DegreesToRadians((double)(lon2 - lon1));
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(DegreesToRadians((double)lat1)) * Math.Cos(DegreesToRadians((double)lat2))
            * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return Math.Round(earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    private static bool IsArrived(double distanceMeters) => distanceMeters <= 30d;
}
