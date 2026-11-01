namespace BonBon.Business.Tracking;

/// <summary>
/// Quy tắc nghiệp vụ thuần cho tracking xe giữa người thuê và chủ xe.
/// Không phụ thuộc database, HTTP hay SignalR để service và hub dùng chung.
/// </summary>
public static class TrackingRules
{
    /// <summary>Chỉ mở tracking trước giờ nhận/trả xe đúng 30 phút.</summary>
    public const int LeadTimeMinutes = 30;

    /// <summary>Xe được coi là đã tới điểm hẹn khi cách tối đa 30 mét.</summary>
    public const double ArrivalRadiusMeters = 30d;

    public const string PickupStage = "pickup";

    public const string ReturnStage = "return";

    public const string CustomerViewer = "Customer";

    public const string OwnerViewer = "Owner";

    public static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(LeadTimeMinutes);

    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    private static readonly HashSet<string> PickupStatuses = ["Confirmed", "ReadyForPickup"];

    private static readonly HashSet<string> ReturnStatuses = ["InProgress"];

    /// <summary>
    /// Người thuê chỉ xem xe tới điểm nhận; chủ xe chỉ xem xe về điểm trả.
    /// </summary>
    public static string? ResolveViewerRole(Guid accountId, Guid customerAccountId, Guid ownerAccountId)
    {
        if (accountId == ownerAccountId) return OwnerViewer;
        if (accountId == customerAccountId) return CustomerViewer;
        return null;
    }

    public static string GetViewerStage(string viewerRole) =>
        viewerRole == OwnerViewer ? ReturnStage : PickupStage;

    public static bool SupportsStage(string bookingStatus, string stage) =>
        stage == ReturnStage
            ? ReturnStatuses.Contains(bookingStatus)
            : PickupStatuses.Contains(bookingStatus);

    /// <summary>Giai đoạn tracking tương ứng với trạng thái booking hiện tại.</summary>
    public static string? GetCandidateStage(string bookingStatus) =>
        ReturnStatuses.Contains(bookingStatus) ? ReturnStage
        : PickupStatuses.Contains(bookingStatus) ? PickupStage
        : null;

    /// <summary>Mốc giờ nhận xe (pickup) hoặc giờ trả xe (return) của booking.</summary>
    public static DateTimeOffset GetRentalBoundary(string stage, DateTimeOffset startAt, DateTimeOffset endAt) =>
        stage == ReturnStage ? endAt : startAt;

    public static DateTimeOffset GetWindowOpensAt(string stage, DateTimeOffset startAt, DateTimeOffset endAt) =>
        GetRentalBoundary(stage, startAt, endAt) - LeadTime;

    public static string GetBoundaryLabel(string stage) =>
        stage == ReturnStage ? "giờ trả xe" : "giờ nhận xe";

    public static bool IsWindowOpen(string stage, DateTimeOffset startAt, DateTimeOffset endAt, DateTimeOffset now) =>
        now >= GetWindowOpensAt(stage, startAt, endAt);

    public static (decimal Latitude, decimal Longitude)? GetDestination(
        string stage,
        decimal? pickupLatitude,
        decimal? pickupLongitude,
        decimal? returnLatitude,
        decimal? returnLongitude) =>
        stage == ReturnStage
            ? returnLatitude is not null && returnLongitude is not null
                ? (returnLatitude.Value, returnLongitude.Value)
                : null
            : pickupLatitude is not null && pickupLongitude is not null
                ? (pickupLatitude.Value, pickupLongitude.Value)
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

    public static string FormatVietnamTime(DateTimeOffset value) =>
        value.ToOffset(VietnamOffset).ToString("HH:mm 'ngày' dd/MM/yyyy");

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
}
