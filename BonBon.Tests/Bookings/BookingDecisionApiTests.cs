using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BonBon.API.Controllers;
using BonBon.Business.Common;
using BonBon.Business.DTOs.Bookings;
using BonBon.Business.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BonBon.Tests.Bookings;

public class BookingDecisionApiTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Booking = Guid.NewGuid();

    private static TestServer CreateServer(IBookingService service) => new(new WebHostBuilder()
        .ConfigureServices(services =>
        {
            // This host never loads Program/appsettings, a DbContext or production credentials.
            services.AddSingleton(service);
            services.AddControllers().AddApplicationPart(typeof(BookingsController).Assembly);
            services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
            services.AddAuthorization();
        })
        .Configure(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }));

    [Theory]
    [InlineData("approve", null, 401)]
    [InlineData("reject", null, 401)]
    [InlineData("approve", "Customer", 403)]
    [InlineData("reject", "Admin", 403)]
    public async Task Requires_authenticated_vehicle_owner(string action, string? role, int expected)
    {
        var service = new Mock<IBookingService>(MockBehavior.Strict);
        using var server = CreateServer(service.Object);
        using var client = server.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Add("X-Test-Role", role);
        var response = await client.PostAsync($"/api/bookings/{Booking}/{action}", null);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Manual_confirm_payment_endpoint_is_not_exposed()
    {
        var service = new Mock<IBookingService>(MockBehavior.Strict);
        using var server = CreateServer(service.Object);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "Customer");
        var response = await client.PostAsync($"/api/bookings/{Booking}/confirm-payment", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Creating_booking_without_displayed_quote_is_rejected()
    {
        var service = new Mock<IBookingService>(MockBehavior.Strict);
        using var server = CreateServer(service.Object);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "Customer");
        var response = await client.PostAsJsonAsync("/api/bookings", new { vehicleId = Guid.NewGuid(), rentalType = "Daily", startAt = DateTimeOffset.UtcNow.AddDays(1), endAt = DateTimeOffset.UtcNow.AddDays(2) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal("QUOTE_REQUIRED", problem!.Title);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Manual_decisions_are_retired_and_do_not_change_bookings(string action)
    {
        var service = new Mock<IBookingService>(MockBehavior.Strict);
        using var server = CreateServer(service.Object);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "VehicleOwner");
        var response = await client.PostAsJsonAsync($"/api/bookings/{Booking}/{action}",
            new { ownerAccountId = Guid.NewGuid(), status = "Confirmed", unitPrice = 1 });
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal("MANUAL_APPROVAL_REMOVED", body!.Title);
        service.VerifyNoOtherCalls();
    }

    private sealed class TestAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Role", out var role))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, Owner.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
