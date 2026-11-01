namespace BonBon.Business.DTOs.Tracking;

/// <summary>Chủ xe gắn thiết bị Traccar của xe vào booking.</summary>
public sealed class SetTrackerDeviceRequest
{
    public long DeviceId { get; init; }
}
