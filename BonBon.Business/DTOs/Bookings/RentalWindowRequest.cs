using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Bookings;

public sealed class RentalWindowRequest
{
    [Required]
    public string RentalType { get; init; } = default!;

    public DateTimeOffset StartAt { get; init; }

    public DateTimeOffset EndAt { get; init; }
}
