using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BonBon.API.Controllers;
using BonBon.Business.Common;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Implementations;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BonBon.Tests.Payments;

public class PaymentApiTests
{
    private static TestServer Host(IPaymentService service, string destination = "https://web.example.test/payment/result") => new(new WebHostBuilder()
        .ConfigureServices(services =>
        {
            services.AddSingleton(service);
            services.Configure<PaymentReturnOptions>(options => options.FrontendResultUrl = destination);
            services.AddControllers().AddApplicationPart(typeof(PaymentController).Assembly);
            services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
            services.AddAuthorization();
        })
        .Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }));

    [Theory]
    [InlineData(null, 401)]
    [InlineData("VehicleOwner", 403)]
    [InlineData("Admin", 403)]
    public async Task Checkout_requires_customer_claim(string? role, int code)
    {
        var service = new Mock<IPaymentService>(MockBehavior.Strict);
        using var host = Host(service.Object);
        using var client = host.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Add("Role", role);
        var response = await client.PostAsJsonAsync("/api/payments", new CreatePaymentRequest(PaymentTestData.Booking));
        Assert.Equal((HttpStatusCode)code, response.StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Checkout_takes_identity_from_claim_and_only_booking_from_body()
    {
        var service = new Mock<IPaymentService>(MockBehavior.Strict);
        var payment = PaymentTestData.Attempt;
        service.Setup(x => x.CreateAsync(PaymentTestData.Customer,
            It.Is<CreatePaymentRequest>(r => r.BookingId == PaymentTestData.Booking), "checkout_request_0001", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<PaymentCheckoutResponse>.Success(new(payment.PaymentId, payment.BookingId, payment.Amount,
                payment.Currency, payment.Status, payment.CheckoutUrl, payment.ExpiresAt, false)));
        using var host = Host(service.Object);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Role", "Customer");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "checkout_request_0001");
        var response = await client.PostAsJsonAsync("/api/payments", new { bookingId = PaymentTestData.Booking, amount = 1, status = "Succeeded", customerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1300000m, (await response.Content.ReadFromJsonAsync<PaymentCheckoutResponse>())!.Amount);
        service.VerifyAll();
    }

    [Theory]
    [InlineData("vnp_Amount=100&vnp_Amount=200")]
    [InlineData("vnp_TxnRef=ABC&vnp_txnref=OTHER")]
    public async Task Duplicate_callback_keys_are_rejected_before_service(string query)
    {
        var service = new Mock<IPaymentService>(MockBehavior.Strict);
        using var host = Host(service.Object);
        using var client = host.CreateClient();
        var response = await client.GetAsync("/api/payments/vnpay/ipn?" + query);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"RspCode\":\"97\"", json);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("PAYMENT_DISABLED", 503)]
    [InlineData("PAYMENT_SCHEMA_UNAVAILABLE", 503)]
    [InlineData("PAYMENT_FORBIDDEN", 403)]
    [InlineData("BOOKING_NOT_FOUND", 404)]
    [InlineData("PAYMENT_IN_PROGRESS", 409)]
    [InlineData("BOOKING_NOT_PAYABLE", 409)]
    public async Task Maps_checkout_errors_to_http(string code, int http)
    {
        var service = new Mock<IPaymentService>();
        service.Setup(x => x.CreateAsync(It.IsAny<Guid>(), It.IsAny<CreatePaymentRequest>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<PaymentCheckoutResponse>.Failure(code, "Thông báo"));
        using var host = Host(service.Object);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Role", "Customer");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "checkout_request_0001");
        var response = await client.PostAsJsonAsync("/api/payments", new CreatePaymentRequest(PaymentTestData.Booking));
        Assert.Equal((HttpStatusCode)http, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_real_service_returns_unavailable_without_database()
    {
        var repository = new Mock<IPaymentRepository>(MockBehavior.Strict);
        var gateway = new Mock<IPaymentGateway>(MockBehavior.Strict);
        var service = new PaymentService(repository.Object, gateway.Object, Options.Create(new PaymentOptions()), new PaymentTestData.FixedClock());
        using var host = Host(service);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Role", "Customer");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "checkout_request_0001");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.PostAsJsonAsync("/api/payments", new CreatePaymentRequest(PaymentTestData.Booking))).StatusCode);
        repository.VerifyNoOtherCalls(); gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Browser_return_redirects_only_to_configured_page_with_verified_id()
    {
        var service = new Mock<IPaymentService>(MockBehavior.Strict);
        var id = Guid.NewGuid();
        service.Setup(x => x.HandleReturnAsync(It.Is<IReadOnlyDictionary<string, string>>(p =>
                p.Count == 1 && p["vnp_TxnRef"] == "SIGNED"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<PaymentReturnResponse>.Success(new(id, "Pending", false, "read only")));
        using var host = Host(service.Object);
        using var client = host.CreateClient();
        var response = await client.GetAsync("/api/payments/vnpay/return/redirect?vnp_TxnRef=SIGNED&paymentId=" +
            Guid.NewGuid() + "&redirectUrl=https%3A%2F%2Fevil.invalid&returnUrl=%2F%2Fevil.invalid&status=Succeeded");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://web.example.test/payment/result?paymentId=" + id, response.Headers.Location!.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        service.VerifyAll(); service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("PAYMENT_RETURN_INVALID", "invalid")]
    [InlineData("PAYMENT_NOT_FOUND", "invalid")]
    [InlineData("PAYMENT_AMOUNT_INVALID", "invalid")]
    [InlineData("PAYMENT_DISABLED", "unavailable")]
    [InlineData("PAYMENT_SCHEMA_UNAVAILABLE", "unavailable")]
    [InlineData("PAYMENT_TEMPORARILY_UNAVAILABLE", "unavailable")]
    public async Task Browser_return_failure_never_forwards_unverified_id_or_status(string code, string marker)
    {
        var service = new Mock<IPaymentService>(MockBehavior.Strict);
        service.Setup(x => x.HandleReturnAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<PaymentReturnResponse>.Failure(code, "not forwarded"));
        using var host = Host(service.Object);
        using var client = host.CreateClient();
        var response = await client.GetAsync("/api/payments/vnpay/return/redirect?paymentId=" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://web.example.test/payment/result?returnError=" + marker, response.Headers.Location!.ToString());
        service.VerifyAll(); service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("//evil.invalid/payment/result")]
    [InlineData("http://web.example.test/payment/result")]
    [InlineData("https://user:password@web.example.test/payment/result")]
    [InlineData("https://web.example.test/other")]
    [InlineData("https://web.example.test/payment/result?next=https://evil.invalid")]
    [InlineData("https://web.example.test/payment/result#fragment")]
    public async Task Invalid_destination_fails_closed_before_service(string destination)
    {
        var service = new Mock<IPaymentService>(MockBehavior.Strict);
        using var host = Host(service.Object, destination);
        using var client = host.CreateClient();
        var response = await client.GetAsync("/api/payments/vnpay/return/redirect");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Headers.Location);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Duplicate_browser_parameters_are_rejected_before_service()
    {
        var service = new Mock<IPaymentService>(MockBehavior.Strict);
        using var host = Host(service.Object);
        using var client = host.CreateClient();
        var response = await client.GetAsync("/api/payments/vnpay/return/redirect?vnp_Amount=1&vnp_Amount=2");
        Assert.Equal("https://web.example.test/payment/result?returnError=invalid", response.Headers.Location!.ToString());
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Json_return_contract_remains_available_without_redirect()
    {
        var service = new Mock<IPaymentService>(MockBehavior.Strict);
        var id = Guid.NewGuid();
        service.Setup(x => x.HandleReturnAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<PaymentReturnResponse>.Success(new(id, "Pending", false, "read only")));
        using var host = Host(service.Object);
        using var client = host.CreateClient();
        var response = await client.GetAsync("/api/payments/vnpay/return?vnp_TxnRef=SIGNED");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(id, (await response.Content.ReadFromJsonAsync<PaymentReturnResponse>())!.PaymentId);
        service.VerifyAll(); service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("tampered")]
    [InlineData("wrong-stored-amount")]
    public async Task Browser_return_runs_real_signature_and_service_without_any_write(string scenario)
    {
        var payment = PaymentTestData.Attempt;
        var repository = new Mock<IPaymentRepository>(MockBehavior.Strict);
        if (scenario != "tampered")
            repository.Setup(x => x.GetByReferenceAsync("ABC123", It.IsAny<CancellationToken>()))
                .ReturnsAsync(scenario == "wrong-stored-amount" ? payment with { Amount = 1 } : payment);
        var gateway = new VnPayGateway(Options.Create(PaymentTestData.GatewayOptions));
        var service = new PaymentService(repository.Object, gateway, Options.Create(new PaymentOptions { Enabled = true }), new PaymentTestData.FixedClock());
        using var host = Host(service);
        using var client = host.CreateClient();
        var query = "vnp_Amount=130000000&vnp_OrderInfo=BonBon+rental+test&vnp_PayDate=20261023090000&" +
            "vnp_ResponseCode=00&vnp_TmnCode=TEST0001&vnp_TransactionNo=12345678&vnp_TransactionStatus=00&vnp_TxnRef=" +
            (scenario == "tampered" ? "FORGED" : "ABC123") +
            "&vnp_SecureHash=95fe75122083b87b8cb00b1340d3fd3d037a9d8952aad15f22727857733ed96798e6fa81bb63991ec543a550cbd8d3b086232423db16f6b5d2fe4f4522aec9d8";
        var response = await client.GetAsync("/api/payments/vnpay/return/redirect?" + query);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://web.example.test/payment/result?" +
            (scenario == "valid" ? "paymentId=" + payment.PaymentId : "returnError=invalid"), response.Headers.Location!.ToString());
        if (scenario != "tampered") repository.Verify(x => x.GetByReferenceAsync("ABC123", It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls(); // Includes CreateAsync and ProcessNotificationAsync: forbidden on Return.
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, PaymentTestData.Customer.ToString()),
                new Claim(ClaimTypes.Role, role.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
