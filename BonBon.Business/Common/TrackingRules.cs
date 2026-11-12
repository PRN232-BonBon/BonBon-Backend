namespace BonBon.Business.Common;

/// <summary>
/// Quy tắc nghiệp vụ thuần cho tracking xe: chủ xe theo dõi vị trí xe trong lúc
/// khách đang thuê. Vị trí do điện thoại của người thuê gửi lên, không dùng thiết bị
/// gắn trên xe. Không phụ thuộc database, HTTP hay SignalR.
/// </summary>
public static class TrackingRules
{
    /// <summary>Trạng thái "đang thuê": giao xe xong là tracking bật, trả xe là tắt.</summary>
    public const string LiveRentalStatus = "InProgress";

    public const string ConfirmedStatus = "Confirmed";

    public const string ReadyForPickupStatus = "ReadyForPickup";

    public const string OwnerViewer = "Owner";

    /// <summary>Xe được coi là đã tới điểm trả khi cách tối đa 30 mét.</summary>
    public const double ArrivalRadiusMeters = 30d;

    /// <summary>Trạng thái booking mà chủ xe còn được thiết lập điểm trả xe.</summary>
    public static readonly string[] ConfigurableStatuses =
        [ConfirmedStatus, ReadyForPickupStatus, LiveRentalStatus];

    /// <summary>Tracking chỉ tồn tại trong thời gian thuê xe.</summary>
    public static bool IsActive(string bookingStatus) => bookingStatus == LiveRentalStatus;

    /// <summary>
    /// Chỉ chủ xe được xem vị trí xe. Người thuê không xem tracking; điện thoại của
    /// người thuê chỉ gửi toạ độ lên.
    /// </summary>
    public static string? ResolveViewerRole(Guid accountId, Guid ownerAccountId) =>
        accountId == ownerAccountId ? OwnerViewer : null;

    /// <summary>Đích tính khoảng cách của chủ xe là điểm trả xe.</summary>
    public static (decimal Latitude, decimal Longitude)? GetDestination(
        decimal? returnLatitude, decimal? returnLongitude) =>
        returnLatitude is not null && returnLongitude is not null
            ? (returnLatitude.Value, returnLongitude.Value)
            : null;

    /// <summary>Khoảng cách đường chim bay giữa hai toạ độ GPS, đơn vị mét.</summary>
    public static double DistanceMeters(decimal latitude1, decimal longitude1, decimal latitude2, decimal longitude2)
    {
        const double earthRadiusMeters = 6_371_000;
        var deltaLatitude = DegreesToRadians((double)(latitude2 - latitude1));
        var deltaLongitude = DegreesToRadians((double)(longitude2 - longitude1));
        var a = Math.Sin(deltaLatitude / 2) * Math.Sin(deltaLatitude / 2)
            + Math.Cos(DegreesToRadians((double)latitude1)) * Math.Cos(DegreesToRadians((double)latitude2))
            * Math.Sin(deltaLongitude / 2) * Math.Sin(deltaLongitude / 2);
        return Math.Round(earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)));
    }

    public static bool IsArrived(double distanceMeters) => distanceMeters <= ArrivalRadiusMeters;

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
}
