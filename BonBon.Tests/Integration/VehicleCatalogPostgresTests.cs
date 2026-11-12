using BonBon.DataAccess.Repositories.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;
using BonBon.Business.Services.Implementations;
using BonBon.Business.DTOs.Vehicles;

namespace BonBon.Tests.Integration;

public class VehicleCatalogPostgresTests
{
    [LocalPostgresTheory]
    [InlineData("Hourly", 4)]
    [InlineData("Daily", 6)]
    [InlineData("Daily", 7)]
    public async Task Catalog_estimate_and_booking_quote_agree(string rentalType, int units)
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync();
        await database.ExecuteAsync("UPDATE bonbon.vehicle SET fuel_type = 'Gasoline', body_type = 'Sedan', day_price = 650001, weekly_discount_enabled = true, weekly_discount_percent = 10");
        await using var context = database.CreateContext();
        var bookings = new BookingService(new BookingRepository(context));
        var vehicles = new VehicleService(new VehicleRepository(context), bookings);
        var start = database.Start.AddDays(5);
        var end = rentalType == "Hourly" ? start.AddHours(units) : start.AddDays(units);
        var catalog = await vehicles.SearchAsync(new VehicleSearchRequest { RentalType = rentalType, StartAt = start, EndAt = end });
        var quote = await bookings.QuoteAsync(new() { VehicleId = database.Vehicle, RentalType = rentalType, StartAt = start, EndAt = end });
        Assert.True(catalog.IsSuccess);
        Assert.True(quote.IsSuccess);
        Assert.Equal(quote.Data!.QuotedRentalAmount, Assert.Single(catalog.Data!).QuotedRentalAmount);
        var created = await bookings.CreateAsync(database.Customer,
            new() { VehicleId = database.Vehicle, RentalType = rentalType, StartAt = start, EndAt = end });
        Assert.True(created.IsSuccess);
        Assert.Equal(quote.Data.QuotedRentalAmount, created.Data!.QuotedRentalAmount);
        await database.ExecuteAsync("UPDATE bonbon.vehicle SET day_price = 900000, hour_price = 120000");
        var detail = await bookings.GetDetailAsync(created.Data.BookingId, database.Customer);
        Assert.True(detail.IsSuccess);
        Assert.Equal(created.Data.QuotedRentalAmount, detail.Data!.QuotedRentalAmount);
    }

    [LocalPostgresTheory]
    [InlineData("empty", true)]
    [InlineData("location", true)]
    [InlineData("seats", true)]
    [InlineData("transmission", true)]
    [InlineData("fuel", true)]
    [InlineData("body", true)]
    [InlineData("min", true)]
    [InlineData("max", true)]
    [InlineData("window", true)]
    [InlineData("combined", true)]
    [InlineData("no-results", false)]
    [InlineData("blocked", false)]
    public async Task Nullable_filters_execute_on_real_postgres(string scenario, bool expected)
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync();
        await database.ExecuteAsync("UPDATE bonbon.vehicle SET fuel_type = 'Gasoline', body_type = 'Sedan'");
        if (scenario == "blocked")
            await database.ExecuteAsync($"UPDATE bonbon.booking SET status = 'Confirmed' WHERE booking_id = '{database.FirstBooking}'");
        var filter = new VehicleSearchFilter(null, null, null, null, null, null, null, null, null);
        filter = scenario switch
        {
            "location" => filter with { Location = "address" },
            "seats" => filter with { Seats = 4 },
            "transmission" => filter with { Transmission = "Automatic" },
            "fuel" => filter with { FuelType = "Gasoline" },
            "body" => filter with { BodyType = "Sedan" },
            "min" => filter with { MinPrice = 650000 },
            "max" => filter with { MaxPrice = 650000 },
            "window" or "blocked" => filter with { StartAt = database.Start.ToOffset(TimeSpan.FromHours(7)), EndAt = database.Start.AddDays(1) },
            "combined" => new("address", database.Start, database.Start.AddDays(1), 4, "Automatic", "Gasoline", "Sedan", 650000, 650000),
            "no-results" => filter with { Location = "not-a-real-city" },
            _ => filter,
        };
        await using var context = database.CreateContext();
        var result = await new VehicleRepository(context).SearchAsync(filter);
        if (expected) Assert.Equal(database.Vehicle, Assert.Single(result).Id);
        else Assert.Empty(result);
    }
}
