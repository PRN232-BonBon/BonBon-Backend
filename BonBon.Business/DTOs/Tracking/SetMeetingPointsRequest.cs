using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Tracking;

/// <summary>Chủ xe thiết lập điểm nhận và điểm trả xe trước khi tracking mở.</summary>
public sealed class SetMeetingPointsRequest
{
    [Range(typeof(decimal), "-90", "90")]
    public required decimal PickupLatitude { get; init; }

    [Range(typeof(decimal), "-180", "180")]
    public required decimal PickupLongitude { get; init; }

    [Range(typeof(decimal), "-90", "90")]
    public required decimal ReturnLatitude { get; init; }

    [Range(typeof(decimal), "-180", "180")]
    public required decimal ReturnLongitude { get; init; }
}
