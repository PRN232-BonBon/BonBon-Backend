using System.Text.Json;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BonBon.API.Controllers;
using BonBon.API.Infrastructure;
using BonBon.Business.Services.Implementations;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess;
using BonBon.DataAccess.Repositories.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BonBon.Tests.Integration;

public class VehicleResubmissionPostgresTests
{
    [LocalPostgresFact]
    public async Task Http_resubmit_keeps_original_vehicle_and_reaches_real_database()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await PrepareAsync(db);
        using var server = new TestServer(new WebHostBuilder().ConfigureServices(services => {
            services.AddDbContext<BonBonDbContext>(options => options.UseNpgsql(db.ConnectionString));
            services.AddScoped<IVehicleRepository, VehicleRepository>();
            services.AddScoped<IVehicleService, VehicleService>();
            services.AddSingleton(new Mock<IBookingService>().Object);
            services.AddSingleton(new VehicleImageProcessor());
            services.AddControllers().AddApplicationPart(typeof(VehiclesController).Assembly);
            services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => {});
            services.AddAuthorization();
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(x => x.MapControllers()); }));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("Test-Account", db.Outsider.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/vehicles/registrations/{db.Vehicle}/rejected")).StatusCode);
        client.DefaultRequestHeaders.Remove("Test-Account"); client.DefaultRequestHeaders.Add("Test-Account", db.Owner.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/vehicles/registrations/{db.Vehicle}/rejected")).StatusCode);
        var data = Submission().Vehicle;
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web))), "metadata");
        var response = await client.PostAsync($"/api/vehicles/registrations/{db.Vehicle}/resubmit", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PendingApproval", await db.ScalarAsync<string>($"SELECT status FROM bonbon.vehicle WHERE vehicle_id='{db.Vehicle}'"));
        Assert.Equal("/synthetic-original.webp", await db.ScalarAsync<string>($"SELECT image_url FROM bonbon.vehicle_image WHERE vehicle_id='{db.Vehicle}'"));
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Test-Account", out var account)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
    private static VehicleRegistrationSubmission Submission(params StoredImage[] images) => new(
        new VehicleRegistrationData("TEST-001", "Toyota", "Vios", 2022, 5, "Automatic", "Gasoline", "Sedan",
            "Xe sạch sẽ được bảo dưỡng đầy đủ.", "123 Nguyễn Huệ, Quận 1", 100000, 700000,
            false, null, true, 400, 3000, null, ["Bluetooth"]), images);

    private static async Task PrepareAsync(DisposablePostgresDatabase db)
    {
        await db.ExecuteAsync($"""
            UPDATE bonbon.vehicle SET status='Rejected', rejection_reason='Ảnh chưa rõ', fuel_type='Gasoline',
                body_type='Sedan', description='Xe sạch sẽ được bảo dưỡng đầy đủ.', production_year=2022
            WHERE vehicle_id='{db.Vehicle}';
            INSERT INTO bonbon.vehicle_image(vehicle_id, image_url, is_cover, sort_order)
            VALUES ('{db.Vehicle}', '/synthetic-original.webp', true, 0)
            """);
    }

    [LocalPostgresFact]
    public async Task Rejected_owner_can_resubmit_same_vehicle_and_admin_can_approve_it()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await PrepareAsync(db);
        await db.ExecuteAsync($"UPDATE bonbon.account_role SET status='Pending' WHERE account_id='{db.Owner}' AND role_id=3");
        await using var context = db.CreateContext();
        var repository = new VehicleRepository(context);
        Assert.Null(await repository.GetRejectedRegistrationJsonAsync(db.Outsider, db.Vehicle));
        Assert.Null(await repository.ResubmitAsync(db.Outsider, db.Vehicle, Submission()));
        using var original = JsonDocument.Parse((await repository.GetRejectedRegistrationJsonAsync(db.Owner, db.Vehicle))!);
        Assert.Equal("Ảnh chưa rõ", original.RootElement.GetProperty("rejectionReason").GetString());
        Assert.Equal("/synthetic-original.webp", original.RootElement.GetProperty("imageUrls")[0].GetString());
        var result = await repository.ResubmitAsync(db.Owner, db.Vehicle, Submission());
        Assert.Equal(db.Vehicle, result!.VehicleId);
        Assert.Equal("PendingApproval", result.Status);
        Assert.Equal(1, result.ImageCount);
        Assert.Null(await repository.ResubmitAsync(db.Owner, db.Vehicle, Submission()));
        Assert.Equal("/synthetic-original.webp", await db.ScalarAsync<string>($"SELECT image_url FROM bonbon.vehicle_image WHERE vehicle_id='{db.Vehicle}'"));
        Assert.Equal("OK", await new AdminRepository(context).ReviewAsync(db.Owner, db.Vehicle, true, null, default));
        Assert.Equal("Available", await db.ScalarAsync<string>($"SELECT status FROM bonbon.vehicle WHERE vehicle_id='{db.Vehicle}'"));
        Assert.Equal("Active", await db.ScalarAsync<string>($"SELECT status FROM bonbon.account_role WHERE account_id='{db.Owner}' AND role_id=3"));
    }

    [LocalPostgresFact]
    public async Task Replacement_images_and_status_are_atomic_on_failure()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await PrepareAsync(db);
        await using var context = db.CreateContext();
        var repository = new VehicleRepository(context);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => repository.ResubmitAsync(db.Owner, db.Vehicle,
            Submission(new StoredImage([], 1, 1))));
        Assert.Equal("Rejected", await db.ScalarAsync<string>($"SELECT status FROM bonbon.vehicle WHERE vehicle_id='{db.Vehicle}'"));
        Assert.Equal("/synthetic-original.webp", await db.ScalarAsync<string>($"SELECT image_url FROM bonbon.vehicle_image WHERE vehicle_id='{db.Vehicle}'"));
        var replaced = await repository.ResubmitAsync(db.Owner, db.Vehicle, Submission(new StoredImage([1, 2, 3], 10, 10)));
        Assert.Equal(1, replaced!.ImageCount);
        Assert.Equal(3, await db.ScalarAsync<int>($"SELECT octet_length(image_data) FROM bonbon.vehicle_image WHERE vehicle_id='{db.Vehicle}'"));
    }

    [LocalPostgresFact]
    public async Task Concurrent_resubmission_only_one_wins()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await PrepareAsync(db);
        await using var first = db.CreateContext();
        await using var second = db.CreateContext();
        var results = await Task.WhenAll(new VehicleRepository(first).ResubmitAsync(db.Owner, db.Vehicle, Submission()),
            new VehicleRepository(second).ResubmitAsync(db.Owner, db.Vehicle, Submission()));
        Assert.Single(results, value => value is not null);
        Assert.Equal("PendingApproval", await db.ScalarAsync<string>($"SELECT status FROM bonbon.vehicle WHERE vehicle_id='{db.Vehicle}'"));
    }

    [LocalPostgresFact]
    public async Task Suspended_account_cannot_read_or_resubmit_using_an_old_session()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await PrepareAsync(db);
        await db.ExecuteAsync($"UPDATE bonbon.account SET status='Suspended' WHERE account_id='{db.Owner}'");
        await using var context = db.CreateContext();
        var repository = new VehicleRepository(context);
        Assert.Null(await repository.GetRejectedRegistrationJsonAsync(db.Owner, db.Vehicle));
        Assert.Null(await repository.ResubmitAsync(db.Owner, db.Vehicle, Submission()));
        Assert.Equal("Rejected", await db.ScalarAsync<string>($"SELECT status FROM bonbon.vehicle WHERE vehicle_id='{db.Vehicle}'"));
    }
}
