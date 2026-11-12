using BonBon.Business.Common;
using BonBon.Business.Services.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;
using Moq;

namespace BonBon.Tests.Bookings;

public class BillingTests
{
    [Theory]
    [InlineData("Daily", 6, true, 10, 3900000)]
    [InlineData("Daily", 7, true, 10, 4095000)]
    [InlineData("Daily", 7, false, 10, 4550000)]
    [InlineData("Hourly", 8, true, 10, 800000)]
    public void Discount_only_applies_to_daily_rentals_of_at_least_seven_days(
        string type, int units, bool enabled, decimal percent, decimal total)
    {
        var result = RentalPricing.Calculate(type, units, 100000, 650000, enabled, percent);
        Assert.Equal(total, result.TotalAmount);
    }

    [Fact]
    public void Vnd_discount_rounds_away_from_zero()
    {
        Assert.Equal(1m, RentalPricing.Calculate("Daily", 7, 1, 1, true, 10).DiscountAmount);
        Assert.Equal(2m, MileagePricing.Calculate(0, 2.0005m, 2, 3000).Fee);
    }

    [Theory]
    [InlineData(null, null, 0, 0)]
    [InlineData(400, 3000, 150, 450000)]
    [InlineData(800, 3000, 0, 0)]
    public void Mileage_uses_booking_snapshot(int? included, int? rate, decimal excess, decimal fee)
    {
        var result = MileagePricing.Calculate(10000, 10550, included, rate);
        Assert.Equal(550, result.ActualDistanceKm);
        Assert.Equal(excess, result.ExcessKm);
        Assert.Equal(fee, result.Fee);
    }

    internal static BookingDetailRecord Booking(Guid customer, Guid owner, Guid booking, Guid charge) => new(
        booking, "Returned", "Daily", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1),
        1, 650000, 0, 0, 650000, 400, 3000, null, "VND", DateTimeOffset.UtcNow,
        Guid.NewGuid(), "Toyota", "Vios", "TEST", null, "Test address", 4, "Automatic", "Gasoline",
        customer, "Customer", "customer@test.invalid", null, owner, "Owner", "owner@test.invalid", null,
        [], [new(charge, booking, "EXCESS_MILEAGE", "Mileage", 450000, "VND", "Proposed", DateTimeOffset.UtcNow)]);

    [Fact]
    public async Task Reading_excess_mileage_reuses_charge_and_never_writes()
    {
        var customer = Guid.NewGuid(); var id = Guid.NewGuid(); var chargeId = Guid.NewGuid();
        var repository = new Mock<IBookingRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Booking(customer, Guid.NewGuid(), id, chargeId));
        repository.Setup(x => x.GetInspectionAsync(id, "Pickup", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InspectionRecord(Guid.NewGuid(), id, Guid.NewGuid(), "Pickup", 10000, null, null, DateTimeOffset.UtcNow));
        repository.Setup(x => x.GetInspectionAsync(id, "Return", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InspectionRecord(Guid.NewGuid(), id, Guid.NewGuid(), "Return", 10550, null, null, DateTimeOffset.UtcNow));
        var service = new BookingService(repository.Object);
        for (var i = 0; i < 2; i++)
        {
            var response = await service.CalculateExcessMileageAsync(id, customer);
            Assert.True(response.IsSuccess);
            Assert.Equal(450000, response.Data!.ExcessFeeAmount);
            Assert.Equal(chargeId, response.Data.ChargeId);
        }
        repository.Verify(x => x.SaveExcessMileageChargeAsync(It.IsAny<Guid>(), It.IsAny<decimal>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
