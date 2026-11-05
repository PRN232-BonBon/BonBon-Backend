namespace BonBon.Tests.Integration;

public class PaymentSamplePostgresTests
{
    [LocalPostgresFact]
    public async Task Sample_seed_is_idempotent_reuses_images_and_does_not_modify_original_vehicle()
    {
        await using var database = await DisposablePostgresDatabase.CreateAsync();
        await database.ExecuteAsync($"INSERT INTO bonbon.vehicle_image (vehicle_id,image_url,is_cover) VALUES ('{database.Vehicle}','/assets/cars/test.webp',true)");
        var script = $"SET bonbon.payment_sample_source = '{database.Vehicle}';\n"
            + await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "payment_sample.sql"));
        await database.ExecuteAsync(script);
        await database.ExecuteAsync(script);
        const string id = "90000000-0000-0000-0000-000000005000";
        Assert.Equal(1L, await database.ScalarAsync<long>($"SELECT count(*) FROM bonbon.vehicle WHERE vehicle_id='{id}'"));
        Assert.Equal(5000m, await database.ScalarAsync<decimal>($"SELECT day_price FROM bonbon.vehicle WHERE vehicle_id='{id}'"));
        Assert.False(await database.ScalarAsync<bool>($"SELECT weekly_discount_enabled FROM bonbon.vehicle WHERE vehicle_id='{id}'"));
        Assert.Equal(1L, await database.ScalarAsync<long>($"SELECT count(*) FROM bonbon.vehicle_image WHERE vehicle_id='{id}' AND image_data IS NULL AND image_url='/assets/cars/test.webp'"));
        Assert.Equal(650000m, await database.ScalarAsync<decimal>($"SELECT day_price FROM bonbon.vehicle WHERE vehicle_id='{database.Vehicle}'"));
        Assert.Equal(1300000m, await database.ScalarAsync<decimal>($"SELECT quoted_rental_amount FROM bonbon.booking WHERE booking_id='{database.FirstBooking}'"));
    }
}
