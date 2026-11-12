using BonBon.Business.DTOs.Vehicles;
using BonBon.Business.Services.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;
using Moq;

namespace BonBon.Tests.Vehicles;

public class VehicleSearchTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("type")]
    [InlineData("dates")]
    [InlineData("seats")]
    [InlineData("transmission")]
    [InlineData("fuel")]
    [InlineData("body")]
    [InlineData("negative")]
    [InlineData("reversed-price")]
    public async Task Invalid_search_is_rejected_before_repository(string scenario)
    {
        var repository = new Mock<IVehicleRepository>(MockBehavior.Strict);
        var service = new VehicleService(repository.Object,
            new BookingService(new Mock<IBookingRepository>(MockBehavior.Strict).Object));
        var request = scenario switch
        {
            "start" => new VehicleSearchRequest { StartAt = DateTimeOffset.UtcNow.AddDays(1) },
            "end" => new() { EndAt = DateTimeOffset.UtcNow.AddDays(2) },
            "type" => new() { RentalType = "Daily" },
            "dates" => new() { StartAt = DateTimeOffset.UtcNow.AddDays(1), EndAt = DateTimeOffset.UtcNow.AddDays(2) },
            "seats" => new() { Seats = 3 },
            "transmission" => new() { Transmission = "unknown" },
            "fuel" => new() { FuelType = "unknown" },
            "body" => new() { BodyType = "unknown" },
            "negative" => new() { MinPrice = -1 },
            _ => new() { MinPrice = 2, MaxPrice = 1 },
        };
        Assert.False((await service.SearchAsync(request)).IsSuccess);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Empty_search_remains_supported()
    {
        var repository = new Mock<IVehicleRepository>(MockBehavior.Strict);
        repository.Setup(x => x.SearchAsync(It.Is<VehicleSearchFilter>(f => f.Location == null && f.StartAt == null && f.Seats == null),
            It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<VehicleCatalogRecord>());
        var service = new VehicleService(repository.Object, new BookingService(new Mock<IBookingRepository>().Object));
        var result = await service.SearchAsync(new VehicleSearchRequest());
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Data!);
    }
}
