using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BonBon.API.Controllers;
using BonBon.Business.Common;
using BonBon.Business.DTOs.Bookings;
using BonBon.Business.Services.Interfaces;
using BonBon.Business.Services.Implementations;
using BonBon.DataAccess;
using BonBon.DataAccess.Repositories.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BonBon.Tests.Integration;

public sealed class BookingRouteTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    [Fact]
    public async Task Booking_list_selects_participant_view_using_authenticated_account()
    {
        var service = new Mock<IBookingService>(MockBehavior.Strict);
        service.Setup(x => x.GetMineAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<IReadOnlyList<CustomerBookingResponse>>.Success([]));
        service.Setup(x => x.GetOwnerBookingsAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<IReadOnlyList<OwnerBookingResponse>>.Success([]));
        using var server = new TestServer(new WebHostBuilder().ConfigureServices(s => {
            s.AddSingleton(service.Object);
            s.AddControllers().AddApplicationPart(typeof(BookingsController).Assembly);
            s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            s.AddAuthorization();
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(e => e.MapControllers()); }));
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/bookings?participantRole=customer")).StatusCode);
        client.DefaultRequestHeaders.Add("Test-Account", AccountId.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/bookings?participantRole=customer")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/bookings?participantRole=owner")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/bookings?participantRole=admin")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/bookings")).StatusCode);
        service.VerifyAll();
        service.VerifyNoOtherCalls();
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers["Test-Account"].ToString(), out var account)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    [LocalPostgresFact]
    public async Task New_routes_support_booking_flow_against_disposable_postgres()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"DELETE FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}'");
        foreach (var file in new[] { "004_vnpay_sandbox.sql", "005_booking_quote_guard.sql", "006_momo_sandbox.sql", "007_sepay_sandbox.sql", "012_weekly_discount_duration.sql" })
            await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", file)));
        using var server = new TestServer(new WebHostBuilder().ConfigureServices(s => {
            s.AddDbContext<BonBonDbContext>(o => o.UseNpgsql(db.ConnectionString));
            s.AddScoped<IBookingRepository, BookingRepository>();
            s.AddScoped<IBookingService, BookingService>();
            s.AddControllers().AddApplicationPart(typeof(BookingsController).Assembly);
            s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            s.AddAuthorization();
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(e => e.MapControllers()); }));
        using var client = server.CreateClient();
        var window = new { rentalType = "Daily", startAt = db.Start, endAt = db.Start.AddDays(1) };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/rentals/time-validations", window)).StatusCode);
        var request = new BookingQuoteRequest { VehicleId = db.Vehicle, RentalType = "Daily", StartAt = db.Start, EndAt = db.Start.AddDays(1) };
        var response = await client.PostAsJsonAsync("/api/bookings/price-estimates", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var quote = (await response.Content.ReadFromJsonAsync<BookingQuoteResponse>())!;
        Assert.True(quote.IsAvailable);
        client.DefaultRequestHeaders.Add("Test-Account", db.Customer.ToString());
        var create = new CreateBookingRequest { VehicleId = db.Vehicle, RentalType = "Daily", StartAt = db.Start, EndAt = db.Start.AddDays(1),
            ExpectedQuote = new(quote.BillableUnits, quote.UnitPrice, quote.WeeklyDiscountPercent, quote.DiscountAmount, quote.QuotedRentalAmount,
                quote.IncludedMileageKm, quote.OverageFeePerKm, quote.RentalTerms) };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/bookings", create)).StatusCode);
        var query = $"rentalType=Daily&startAt={Uri.EscapeDataString(db.Start.ToString("O"))}&endAt={Uri.EscapeDataString(db.Start.AddDays(1).ToString("O"))}";
        var availability = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/vehicles/{db.Vehicle}/availability?{query}");
        Assert.False(availability.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/bookings?participantRole=customer")).StatusCode);
        client.DefaultRequestHeaders.Remove("Test-Account");
        client.DefaultRequestHeaders.Add("Test-Account", db.Owner.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/bookings?participantRole=owner")).StatusCode);
    }

    [Fact]
    public async Task Availability_uses_route_and_query_filters_and_keeps_legacy_route()
    {
        var vehicle = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2030-01-01T09:00:00Z");
        var end = start.AddDays(1);
        var service = new Mock<IBookingService>(MockBehavior.Strict);
        service.Setup(x => x.IsAvailableAsync(vehicle, It.Is<RentalWindowRequest>(r =>
                r.RentalType == "Daily" && r.StartAt == start && r.EndAt == end), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<bool>.Success(true));
        using var server = new TestServer(new WebHostBuilder().ConfigureServices(s => {
            s.AddSingleton(service.Object);
            s.AddControllers().AddApplicationPart(typeof(BookingsController).Assembly);
            s.AddAuthorization();
        }).Configure(app => { app.UseRouting(); app.UseAuthorization(); app.UseEndpoints(e => e.MapControllers()); }));
        using var client = server.CreateClient();
        var filters = "rentalType=Daily&startAt=2030-01-01T09%3A00%3A00Z&endAt=2030-01-02T09%3A00%3A00Z";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/vehicles/{vehicle}/availability?{filters}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/bookings/availability?vehicleId={vehicle}&{filters}")).StatusCode);
        service.VerifyAll();
    }
}
