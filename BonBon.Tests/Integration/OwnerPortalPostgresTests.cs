using BonBon.DataAccess.Repositories.Implementations;

namespace BonBon.Tests.Integration;

public class OwnerPortalPostgresTests
{
    [LocalPostgresFact]
    public async Task Vehicle_cards_include_private_pending_cars_and_do_not_leak_another_owner()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync();
        await database.ExecuteAsync($"""
            UPDATE bonbon.vehicle SET status = 'PendingApproval', production_year = 2022 WHERE vehicle_id = '{database.Vehicle}';
            INSERT INTO bonbon.vehicle_image (vehicle_id, image_url, sort_order, is_cover)
            VALUES ('{database.Vehicle}', '/test-owner-cover.webp', 0, true);
            """);
        await using var context = database.CreateContext();
        var repository = new VehicleRepository(context);
        var car = Assert.Single(await repository.GetMineAsync(database.Owner));
        Assert.Equal("PendingApproval", car.Status);
        Assert.Equal("/test-owner-cover.webp", car.CoverImageUrl);
        Assert.Equal(1, car.ImageCount);
        Assert.Equal(650000, car.DayPrice);
        Assert.Equal(2022, car.ProductionYear);
        Assert.Equal("", car.BodyType); // Legacy records can predate the optional body-type field.
        Assert.Empty(await repository.GetMineAsync(database.Outsider));
    }

    [LocalPostgresFact]
    public async Task Calendar_blocks_are_owner_scoped_and_use_half_open_overlap()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync();
        await database.ExecuteAsync($"""
            INSERT INTO bonbon.availability_block (vehicle_id, start_at, end_at, reason)
            VALUES ('{database.Vehicle}', '{database.Start:O}', '{database.Start.AddDays(1):O}', 'Bảo dưỡng');
            """);
        await using var context = database.CreateContext();
        var repository = new VehicleRepository(context);
        Assert.Single(await repository.GetOwnerBlocksAsync(database.Owner, database.Start, database.Start.AddDays(2)));
        Assert.Empty(await repository.GetOwnerBlocksAsync(database.Outsider, database.Start, database.Start.AddDays(2)));
        Assert.Empty(await repository.GetOwnerBlocksAsync(database.Owner, database.Start.AddDays(1), database.Start.AddDays(2)));
    }
}
