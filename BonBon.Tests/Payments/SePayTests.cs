using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BonBon.API.Controllers;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Implementations;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;

namespace BonBon.Tests.Payments;

public static class SePayTestData
{
    // Synthetic unit-test values. Never loaded into the running API.
    public static SePayOptions Options => new() { Enabled=true, MerchantId="SYNTHETIC_TEST", MerchantSecretKey="spsk_test_synthetic_not_a_real_merchant_key",
        IpnSecretKey="synthetic-ipn-key-not-a-provider-credential", PublicOrigin="https://api.sepay-test.example",
        ReturnUrl="https://api.sepay-test.example/api/payments/sepay/return", FrontendResultUrl="https://web.sepay-test.example/payment/result" };
    public static DateTimeOffset Now => new(2026,11,16,3,0,0,TimeSpan.Zero);
    public sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow()=>now; }
    public sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> handle) : HttpMessageHandler {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {Interlocked.Increment(ref Calls);return Task.FromResult(handle(request));}
    }
    public static PaymentAttempt Payment => new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid().ToString("N"),1300000,"VND","Pending",Now.AddMinutes(-1),Now.AddMinutes(14),null);
    public static SePayOrder Order(PaymentAttempt p,DateTimeOffset? now=null) => new(Guid.NewGuid().ToString(),"SEPAY-SYNTHETIC",p.Reference,"CAPTURED","1300000.00","VND",
        [new(Guid.NewGuid().ToString(),"PAYMENT","APPROVED","1300000","VND",(now??Now).ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm:ss"))]);
    public static SePayIpn Ipn(SePayOrder order,DateTimeOffset? now=null)=>new((now??Now).ToUnixTimeSeconds(),"ORDER_PAID",order,order.Transactions![0]);
}

public class SePayTests
{
    [Fact] public void Short_ipn_secret_requires_explicit_demo_opt_in() {
        var o=SePayTestData.Options;
        o.IpnSecretKey="synthetic-12";
        Assert.False(o.IsValid());
        o.AllowDemoProduction=true;
        Assert.True(o.IsValid());
        o.SandboxApiBaseUrl="https://pgapi.sepay.vn";
        Assert.False(o.IsValid());
    }
    [Fact] public void Signature_matches_independent_Node_hmac_vector_and_Base64() {
        var signature=new SePaySignatureService();var fields=new Dictionary<string,string>{{"currency","VND"},{"merchant","TEST"},{"order_amount","1300000"}};
        const string expected="I6a1AEm4wbmkyufWqJQ2ajAO3POl1eElBxFANY27cqA=";
        Assert.Equal(expected,signature.Sign(fields,"synthetic-signature-key-only"));Assert.True(signature.Verify(fields,"synthetic-signature-key-only",expected));
        fields["lang"]="en";fields["customer_id"]="0";Assert.Equal(expected,signature.Sign(fields,"synthetic-signature-key-only"));
        fields["order_amount"]="1300001";Assert.False(signature.Verify(fields,"synthetic-signature-key-only",expected));Assert.False(signature.Verify(fields,"synthetic-signature-key-only","bad"));
    }
    [Fact] public void Checkout_is_ordered_form_POST_not_checkoutUrl_and_only_allowed_fields_are_signed() {
        using var g=new SePayGateway(new HttpClient(new SePayTestData.Handler(_=>throw new Exception("No network"))),Options.Create(SePayTestData.Options),new SePaySignatureService());
        var form=g.BuildForm(SePayTestData.Payment);Assert.Equal("POST",form.Method);Assert.Equal(SePayOptions.CheckoutEndpoint,form.Action);
        Assert.Equal(new[]{"order_amount","merchant","currency","operation","order_description","order_invoice_number","success_url","error_url","cancel_url","lang","signature"},form.Fields.Select(f=>f.Name));
        var fields=form.Fields.ToDictionary(f=>f.Name,f=>f.Value);Assert.Equal("1300000",fields["order_amount"]);
        Assert.True(new SePaySignatureService().Verify(fields,SePayTestData.Options.MerchantSecretKey,fields["signature"]));
        Assert.DoesNotContain(SePayTestData.Options.MerchantSecretKey,JsonSerializer.Serialize(form));Assert.DoesNotContain(SePayTestData.Options.IpnSecretKey,JsonSerializer.Serialize(form));
    }
    [Theory] [InlineData("credentials")] [InlineData("live")] [InlineData("same-secret")] [InlineData("http")] [InlineData("localhost")] [InlineData("open-redirect")] [InlineData("endpoint")]
    public void Configuration_fails_closed(string scenario) {
        var o=SePayTestData.Options;Assert.True(o.IsValid());
        switch(scenario) {case "credentials":o.MerchantId="";break;case "live":o.MerchantSecretKey="spsk_live_synthetic_not_real";break;case "same-secret":o.IpnSecretKey=o.MerchantSecretKey;break;
            case "http":o.PublicOrigin="http://api.sepay-test.example";break;case "localhost":o.PublicOrigin="https://127.0.0.1";break;case "open-redirect":o.FrontendResultUrl+="?next=https://other.example";break;case "endpoint":o.SandboxApiBaseUrl="https://pgapi.sepay.vn";break;}
        Assert.False(o.IsValid());
    }
    [Theory] [InlineData(401,"MERCHANT_AUTH_FAILED")] [InlineData(403,"MERCHANT_AUTH_FAILED")] [InlineData(429,"RATE_LIMITED")] [InlineData(404,"ORDER_NOT_FOUND")] [InlineData(500,"GATEWAY_UNAVAILABLE")]
    public async Task REST_errors_do_not_retry_or_create_orders(int status,string code) {
        var handler=new SePayTestData.Handler(r=>{Assert.Equal(HttpMethod.Get,r.Method);Assert.StartsWith("https://pgapi-sandbox.sepay.vn/v1/order/detail/",r.RequestUri!.AbsoluteUri);Assert.Equal("Basic",r.Headers.Authorization!.Scheme);return new((HttpStatusCode)status);});
        using var g=new SePayGateway(new HttpClient(handler),Options.Create(SePayTestData.Options),new SePaySignatureService());
        Assert.Equal(code,(await g.QueryAsync(Guid.NewGuid().ToString("N"),default)).Error);Assert.Equal(1,handler.Calls);
    }
    [Fact] public async Task REST_timeout_or_invalid_JSON_stays_uncertain() {
        foreach(var timeout in new[]{true,false}) {
            var h=new SePayTestData.Handler(_=>timeout?throw new TaskCanceledException():new(HttpStatusCode.OK){Content=new StringContent("not-json")});
            using var g=new SePayGateway(new HttpClient(h),Options.Create(SePayTestData.Options),new SePaySignatureService());
            Assert.Equal(timeout?"QUERY_UNCERTAIN":"INVALID_RESPONSE",(await g.QueryAsync(Guid.NewGuid().ToString("N"),default)).Error);Assert.Equal(1,h.Calls);
        }
    }
    private static (Mock<ISePayPaymentRepository> Repo,Mock<IPaymentRepository> Payments,Mock<ISePayGateway> Gateway,PaymentAttempt Payment,SePayOrder Order) Fixture() {
        var r=new Mock<ISePayPaymentRepository>();var p=new Mock<IPaymentRepository>();r.SetupGet(x=>x.Payments).Returns(p.Object);
        var g=new Mock<ISePayGateway>();g.SetupGet(x=>x.IsConfigured).Returns(true);g.SetupGet(x=>x.MerchantBinding).Returns("sepay:form:synthetic");
        var payment=SePayTestData.Payment with{CheckoutUrl="sepay:form:synthetic"};var order=SePayTestData.Order(payment);
        p.Setup(x=>x.GetByReferenceAsync(payment.Reference,It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        g.Setup(x=>x.QueryAsync(payment.Reference,It.IsAny<CancellationToken>())).ReturnsAsync(new SePayQueryResult(order));
        p.Setup(x=>x.ProcessNotificationAsync(It.IsAny<VerifiedPaymentNotification>(),It.IsAny<DateTimeOffset>(),It.IsAny<Func<PaymentNotificationContext,PaymentNotificationDecision>>(),It.IsAny<CancellationToken>())).ReturnsAsync(PaymentNotificationOutcome.Applied);
        return(r,p,g,payment,order);
    }
    [Theory] [InlineData("amount")] [InlineData("currency")] [InlineData("invoice")] [InlineData("merchant")] [InlineData("transaction")]
    public async Task Invalid_provider_evidence_never_updates_booking(string scenario) {
        var f=Fixture();var o=f.Order;var ipn=SePayTestData.Ipn(o);
        switch(scenario) {case "amount":o=o with{Amount="1"};break;case "currency":o=o with{Currency="USD"};break;case "invoice":o=o with{Invoice=Guid.NewGuid().ToString("N")};break;case "merchant":o=o with{Merchant="DIFFERENT"};break;case "transaction":ipn=ipn with{Transaction=ipn.Transaction! with{Amount="1"}};break;}
        f.Gateway.Setup(x=>x.QueryAsync(f.Payment.Reference,It.IsAny<CancellationToken>())).ReturnsAsync(new SePayQueryResult(o));
        var service=new SePayReconciliationService(f.Repo.Object,f.Gateway.Object,Options.Create(SePayTestData.Options),new SePayTestData.Clock(SePayTestData.Now));
        Assert.Equal(400,await service.ApplyAsync(f.Payment.Reference,ipn,default));
        f.Payments.Verify(x=>x.ProcessNotificationAsync(It.IsAny<VerifiedPaymentNotification>(),It.IsAny<DateTimeOffset>(),It.IsAny<Func<PaymentNotificationContext,PaymentNotificationDecision>>(),It.IsAny<CancellationToken>()),Times.Never);
    }
    [Theory] [InlineData(false)] [InlineData(true)] public async Task Verified_IPN_and_admin_query_apply_same_atomic_policy(bool duplicate) {
        var f=Fixture();f.Payments.Setup(x=>x.ProcessNotificationAsync(It.IsAny<VerifiedPaymentNotification>(),It.IsAny<DateTimeOffset>(),It.IsAny<Func<PaymentNotificationContext,PaymentNotificationDecision>>(),It.IsAny<CancellationToken>()))
            .Callback<VerifiedPaymentNotification,DateTimeOffset,Func<PaymentNotificationContext,PaymentNotificationDecision>,CancellationToken>((n,now,decide,ct)=>{
                Assert.True(n.IsSuccess);Assert.NotNull(n.SePayEvidence);Assert.Equal(PaymentNotificationAction.Success,decide(new(f.Payment,"AwaitingPayment",1300000,now.AddDays(1),true,false,false,now)).Action);
                Assert.Equal(PaymentNotificationAction.ReconcileSuccess,decide(new(f.Payment,"AwaitingPayment",1300000,now.AddDays(1),true,false,false,now.AddHours(1))).Action);
            }).ReturnsAsync(duplicate?PaymentNotificationOutcome.Duplicate:PaymentNotificationOutcome.Applied);
        var s=new SePayReconciliationService(f.Repo.Object,f.Gateway.Object,Options.Create(SePayTestData.Options),new SePayTestData.Clock(SePayTestData.Now));
        Assert.Equal(200,await s.ApplyAsync(f.Payment.Reference,SePayTestData.Ipn(f.Order),default));Assert.Equal(200,await s.ReconcileAsync(f.Payment.Reference,default));
    }
    [Theory] [InlineData("bad-secret",401)] [InlineData("missing-secret",401)] [InlineData("type",400)] [InlineData("future",400)]
    public async Task Ipn_authentication_and_timestamp_are_required(string scenario,int status) {
        var f=Fixture();var reconcile=new Mock<ISePayReconciliationService>();var o=SePayTestData.Options;var ipn=SePayTestData.Ipn(f.Order);var secret=o.IpnSecretKey;
        if(scenario=="bad-secret")secret=o.MerchantSecretKey;if(scenario=="missing-secret")secret="";if(scenario=="type")ipn=ipn with{NotificationType="UNKNOWN"};if(scenario=="future")ipn=ipn with{Timestamp=SePayTestData.Now.AddHours(1).ToUnixTimeSeconds()};
        var handler=new SePayIpnHandler(Options.Create(o),f.Gateway.Object,reconcile.Object,f.Repo.Object,new SePayTestData.Clock(SePayTestData.Now));
        Assert.Equal(status,await handler.HandleAsync(secret,ipn,default));reconcile.Verify(x=>x.ApplyAsync(It.IsAny<string>(),It.IsAny<SePayIpn>(),It.IsAny<CancellationToken>()),Times.Never);
    }
    [Fact] public async Task Ipn_valid_secret_routes_to_merchant_query_and_disabled_short_circuits() {
        var f=Fixture();var reconcile=new Mock<ISePayReconciliationService>();reconcile.Setup(x=>x.ApplyAsync(f.Payment.Reference,It.IsAny<SePayIpn>(),It.IsAny<CancellationToken>())).ReturnsAsync(200);
        var o=SePayTestData.Options;var handler=new SePayIpnHandler(Options.Create(o),f.Gateway.Object,reconcile.Object,f.Repo.Object,new SePayTestData.Clock(SePayTestData.Now));
        Assert.Equal(200,await handler.HandleAsync(o.IpnSecretKey,SePayTestData.Ipn(f.Order),default));o.Enabled=false;Assert.Equal(503,await handler.HandleAsync(o.IpnSecretKey,SePayTestData.Ipn(f.Order),default));
    }
    [Fact] public void Return_redirect_ignores_customer_status_and_never_calls_write_handler() {
        var ipn=new Mock<ISePayIpnHandler>(MockBehavior.Strict);var recon=new Mock<ISePayReconciliationService>(MockBehavior.Strict);var repo=new Mock<ISePayPaymentRepository>(MockBehavior.Strict);
        var controller=new SePayController(ipn.Object,recon.Object,repo.Object,Options.Create(SePayTestData.Options)) {ControllerContext=new(){HttpContext=new DefaultHttpContext()}};
        var id=Guid.NewGuid();controller.Request.QueryString=new("?paymentId="+id+"&status=Succeeded&redirect=https://evil.example");
        var response=Assert.IsType<RedirectResult>(controller.Return(id));Assert.Equal(SePayTestData.Options.FrontendResultUrl+"?provider=SEPAY_SANDBOX&paymentId="+id,response.Url);
    }
    [Theory] [InlineData(PaymentCreateOutcome.Forbidden,"PAYMENT_FORBIDDEN")] [InlineData(PaymentCreateOutcome.InvalidBooking,"BOOKING_NOT_PAYABLE")]
    [InlineData(PaymentCreateOutcome.Inactive,"PAYMENT_ACTOR_INACTIVE")] [InlineData(PaymentCreateOutcome.InProgress,"PAYMENT_IN_PROGRESS")] [InlineData(PaymentCreateOutcome.ReconciliationRequired,"PAYMENT_RECONCILIATION_REQUIRED")]
    public async Task Checkout_preserves_existing_booking_security(PaymentCreateOutcome outcome,string code) {
        var f=Fixture();f.Payments.Setup(x=>x.CreateAsync(It.IsAny<Guid>(),It.IsAny<Guid>(),It.IsAny<string>(),It.IsAny<DateTimeOffset>(),It.IsAny<TimeSpan>(),It.IsAny<Func<PaymentAttempt,string>>(),It.IsAny<CancellationToken>())).ReturnsAsync(new PaymentCreateResult(outcome));
        var s=new SePayCheckoutService(f.Repo.Object,f.Gateway.Object,Options.Create(SePayTestData.Options),Options.Create(new PaymentOptions()),new SePayTestData.Clock(SePayTestData.Now));
        Assert.Equal(code,(await s.CreateAsync(Guid.NewGuid(),new(Guid.NewGuid(),"SEPAY_SANDBOX"),"synthetic_key_123456","127.0.0.1",default)).ErrorCode);
    }
    [Theory] [InlineData("disabled","PAYMENT_DISABLED")] [InlineData("config","PAYMENT_CONFIGURATION_INVALID")]
    [InlineData("schema","PAYMENT_SCHEMA_UNAVAILABLE")] [InlineData("database","PAYMENT_TEMPORARILY_UNAVAILABLE")]
    public async Task Checkout_configuration_schema_and_connection_fail_closed(string scenario,string code) {
        var f=Fixture();var options=SePayTestData.Options;
        if(scenario=="disabled")options.Enabled=false;
        if(scenario=="config")f.Gateway.SetupGet(x=>x.IsConfigured).Returns(false);
        if(scenario is "schema" or "database") f.Payments.Setup(x=>x.CreateAsync(It.IsAny<Guid>(),It.IsAny<Guid>(),It.IsAny<string>(),It.IsAny<DateTimeOffset>(),It.IsAny<TimeSpan>(),It.IsAny<Func<PaymentAttempt,string>>(),It.IsAny<CancellationToken>()))
            .ThrowsAsync(scenario=="schema"?new PaymentSchemaUnavailableException():new Npgsql.NpgsqlException("synthetic connection failure"));
        var s=new SePayCheckoutService(f.Repo.Object,f.Gateway.Object,Options.Create(options),Options.Create(new PaymentOptions()),new SePayTestData.Clock(SePayTestData.Now));
        Assert.Equal(code,(await s.CreateAsync(Guid.NewGuid(),new(Guid.NewGuid(),"SEPAY_SANDBOX"),"synthetic_key_123456","127.0.0.1",default)).ErrorCode);
    }
    [Fact] public async Task Unknown_query_state_does_not_write_payment_and_can_be_queried_again() {
        var f=Fixture();f.Gateway.Setup(x=>x.QueryAsync(f.Payment.Reference,It.IsAny<CancellationToken>())).ReturnsAsync(new SePayQueryResult(null,"QUERY_UNCERTAIN"));
        var service=new SePayReconciliationService(f.Repo.Object,f.Gateway.Object,Options.Create(SePayTestData.Options),new SePayTestData.Clock(SePayTestData.Now));
        Assert.Equal(503,await service.ReconcileAsync(f.Payment.Reference,default));Assert.Equal(503,await service.ReconcileAsync(f.Payment.Reference,default));
        f.Payments.Verify(x=>x.ProcessNotificationAsync(It.IsAny<VerifiedPaymentNotification>(),It.IsAny<DateTimeOffset>(),It.IsAny<Func<PaymentNotificationContext,PaymentNotificationDecision>>(),It.IsAny<CancellationToken>()),Times.Never);
    }
}
