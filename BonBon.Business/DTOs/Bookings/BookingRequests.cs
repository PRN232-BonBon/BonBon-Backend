using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Bookings;

public sealed class BookingQuoteRequest : RentalWindowRequestBase
{
    public Guid VehicleId { get; init; }
}

public sealed class CreateBookingRequest : RentalWindowRequestBase
{
    public Guid VehicleId { get; init; }
}

public abstract class RentalWindowRequestBase
{
    [Required]
    public string RentalType { get; init; } = default!;

    public DateTimeOffset StartAt { get; init; }

    public DateTimeOffset EndAt { get; init; }
}

public sealed class ConfirmPaymentRequest
{
    [MaxLength(50)]
    public string? Provider { get; init; } = "PlatformMock";

    [MaxLength(200)]
    public string? ProviderReference { get; init; }
}

public sealed class ConfirmPickupRequest
{
    [Required]
    [Range(0, 100000000, ErrorMessage = "Số km không hợp lệ.")]
    public decimal OdometerKm { get; init; }

    [Range(0, 100, ErrorMessage = "Mức xăng phải từ 0% đến 100%.")]
    public decimal? FuelLevel { get; init; }

    [MaxLength(2000)]
    public string? ConditionNotes { get; init; }

    public DateTimeOffset? InspectedAt { get; init; }
}

public sealed class ConfirmReturnRequest
{
    [Required]
    [Range(0, 100000000, ErrorMessage = "Số km không hợp lệ.")]
    public decimal OdometerKm { get; init; }

    [Range(0, 100, ErrorMessage = "Mức xăng phải từ 0% đến 100%.")]
    public decimal? FuelLevel { get; init; }

    [MaxLength(2000)]
    public string? ConditionNotes { get; init; }

    public DateTimeOffset? InspectedAt { get; init; }
}
