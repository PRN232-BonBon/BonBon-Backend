using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BonBon.API.Controllers;
using BonBon.Business.Common;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BonBon.Tests.Payments;

public sealed class SePayCheckoutApiTests
{
    private static TestServer Server(ISePayCheckoutService checkout)
    {
        var old = new Mock<IPaymentService>(MockBehavior.Strict);
        return new(new WebHostBuilder().ConfigureServices(s => {
            s.AddSingleton(old.Object); s.AddSingleton(checkout); s.Configure<PaymentReturnOptions>(_ => { });
            s.AddControllers().AddApplicationPart(typeof(PaymentController).Assembly);
            s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
            s.AddAuthorization();
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(e => e.MapControllers()); }));
    }

    [Fact]
    public async Task Checkout_uses_customer_claim_and_sepay_only()
    {
        var customer = PaymentTestData.Customer; var booking = PaymentTestData.Booking; var id = Guid.NewGuid();
        var checkout = new Mock<ISePayCheckoutService>(MockBehavior.Strict);
        checkout.Setup(x => x.CreateAsync(customer, It.Is<CreatePaymentRequest>(r => r.BookingId == booking && r.Provider == "SEPAY_SANDBOX"), "checkout_request_0001", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<SePayCheckoutResponse>.Success(new(id, booking, "SEPAY_SANDBOX", 5000, "VND", "Pending", DateTimeOffset.UtcNow.AddMinutes(1), false, null)));
        using var host = Server(checkout.Object); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Role", "Customer"); client.DefaultRequestHeaders.Add("Idempotency-Key", "checkout_request_0001");
        var response = await client.PostAsJsonAsync("/api/payments", new { bookingId = booking, provider = "SEPAY_SANDBOX", amount = 1, customerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5000, (await response.Content.ReadFromJsonAsync<SePayCheckoutResponse>())!.Amount);
        var unsupported = await client.PostAsJsonAsync("/api/payments", new { bookingId = booking, provider = "VNPAY_SANDBOX" });
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        checkout.VerifyAll();
    }

    [Theory] [InlineData(null, 401)] [InlineData("VehicleOwner", 403)]
    public async Task Checkout_requires_customer(string? role, int expected)
    {
        var checkout = new Mock<ISePayCheckoutService>(MockBehavior.Strict);
        using var host = Server(checkout.Object); using var client = host.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Add("Role", role);
        var response = await client.PostAsJsonAsync("/api/payments", new { bookingId = Guid.NewGuid(), provider = "SEPAY_SANDBOX" });
        Assert.Equal(expected, (int)response.StatusCode); checkout.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("PAYMENT_DISABLED", 503)] [InlineData("PAYMENT_SCHEMA_UNAVAILABLE", 503)]
    [InlineData("PAYMENT_FORBIDDEN", 403)] [InlineData("BOOKING_NOT_FOUND", 404)]
    [InlineData("PAYMENT_IN_PROGRESS", 409)] [InlineData("BOOKING_NOT_PAYABLE", 409)]
    public async Task Checkout_maps_service_errors(string code, int expected)
    {
        var checkout = new Mock<ISePayCheckoutService>();
        checkout.Setup(x => x.CreateAsync(It.IsAny<Guid>(), It.IsAny<CreatePaymentRequest>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<SePayCheckoutResponse>.Failure(code, "Không khả dụng"));
        using var host = Server(checkout.Object); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Role", "Customer"); client.DefaultRequestHeaders.Add("Idempotency-Key", "checkout_request_0001");
        var response = await client.PostAsJsonAsync("/api/payments", new { bookingId = Guid.NewGuid(), provider = "SEPAY_SANDBOX" });
        Assert.Equal(expected, (int)response.StatusCode);
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, PaymentTestData.Customer.ToString()), new Claim(ClaimTypes.Role, role.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
