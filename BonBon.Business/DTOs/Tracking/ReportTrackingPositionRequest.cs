using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Tracking;

/// <summary>Điện thoại của người thuê gửi vị trí GPS trong lúc chuyến đang diễn ra.</summary>
public sealed class ReportTrackingPositionRequest
{
    [Range(typeof(decimal), "-90", "90")]
    public required decimal Latitude { get; init; }

    [Range(typeof(decimal), "-180", "180")]
    public required decimal Longitude { get; init; }

    [Range(typeof(decimal), "0", "100000000")]
    public decimal? AccuracyMeters { get; init; }

    /// <summary>Thời điểm điện thoại ghi nhận vị trí; bỏ trống thì lấy giờ server.</summary>
    public DateTimeOffset? RecordedAt { get; init; }
}
