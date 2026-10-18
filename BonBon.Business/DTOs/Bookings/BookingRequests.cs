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
