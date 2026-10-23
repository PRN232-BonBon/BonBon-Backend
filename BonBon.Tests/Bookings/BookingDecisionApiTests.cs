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

    [Theory]
    [InlineData("BOOKING_NOT_FOUND", 404)]
    [InlineData("BOOKING_NOT_OWNER", 403)]
    [InlineData("OWNER_INACTIVE", 403)]
    [InlineData("BOOKING_ID_INVALID", 400)]
    [InlineData("BOOKING_STATE_CONFLICT", 409)]
    [InlineData("SCHEDULE_CONFLICT", 409)]
    [InlineData("CUSTOMER_INACTIVE", 409)]
    [InlineData("VEHICLE_UNAVAILABLE", 409)]
    [InlineData("BOOKING_DECISION_RETRY", 409)]
    public async Task Returns_problem_details(string code, int expected)
    {
        var service = new Mock<IBookingService>(MockBehavior.Strict);
        service.Setup(x => x.ApproveAsync(Owner, Booking, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<BookingDecisionResponse>.Failure(code, "Thông báo"));
        using var server = CreateServer(service.Object);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "VehicleOwner");
        var response = await client.PostAsync($"/api/bookings/{Booking}/approve", null);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(code, problem!.Title);
        Assert.Equal(expected, problem.Status);
    }

    [Theory]
    [InlineData("approve", "AwaitingPayment")]
    [InlineData("reject", "Rejected")]
    public async Task Uses_jwt_identity_and_ignores_client_supplied_decision_fields(string action, string status)
    {
        var service = new Mock<IBookingService>(MockBehavior.Strict);
        var result = ServiceResult<BookingDecisionResponse>.Success(new(Booking, status, 500000m, 1000000m, "VND", false));
        if (action == "approve")
            service.Setup(x => x.ApproveAsync(Owner, Booking, It.IsAny<CancellationToken>())).ReturnsAsync(result);
        else
            service.Setup(x => x.RejectAsync(Owner, Booking, It.IsAny<CancellationToken>())).ReturnsAsync(result);
        using var server = CreateServer(service.Object);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "VehicleOwner");
        var response = await client.PostAsJsonAsync($"/api/bookings/{Booking}/{action}",
            new { ownerAccountId = Guid.NewGuid(), status = "Confirmed", unitPrice = 1 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingDecisionResponse>();
        Assert.Equal(status, body!.Status);
        Assert.Equal(1000000m, body.QuotedRentalAmount);
        service.VerifyAll();
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
