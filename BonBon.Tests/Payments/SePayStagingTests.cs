using System.Net;
using System.Net.Http.Json;
using BonBon.API.Configuration;
using BonBon.API.Controllers;
using BonBon.Business.Configuration;
using BonBon.Business.Services.Implementations;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace BonBon.Tests.Payments;

public sealed class SePayStagingTests
{
    private static Dictionary<string,string?> Settings()=>new() {
        ["ConnectionStrings:DefaultConnection"]="Host=sepay-postgres;Port=5432;Database=bonbon_payment_staging;Username=bonbon_payment_staging_runner;Password=synthetic-unit-test-only;Pooling=true;Maximum Pool Size=2;Minimum Pool Size=0;Timeout=5;Command Timeout=10",
        ["PaymentStaging:DatabaseProfile"]="SePayDedicated",["PaymentStaging:ExpectedDatabaseHost"]="sepay-postgres",
        ["PaymentStaging:ExpectedDatabaseAddress"]="172.30.92.2",["PaymentStaging:DatabaseMarker"]="bonbon-sepay-staging:v1:5a09765c-c7ec-4e47-b435-63c2a227ea98",
        ["PaymentStaging:TrustedProxyAddresses:0"]="172.30.91.2",["PaymentStaging:PublicOrigin"]="https://api.sepay-staging.example.test",
        ["PaymentStaging:FrontendOrigin"]="",["AllowedHosts"]="api.sepay-staging.example.test;localhost;127.0.0.1",
        ["Jwt:Key"]=new string('X',64),["Jwt:Issuer"]="BonBon.SePayStaging",["Jwt:Audience"]="BonBon.SePayStaging.Web",
        ["Payment:Enabled"]="false",["SePay:Enabled"]="false",["PaymentLocal:IpnSimulationEnabled"]="false"
    };
    private static IConfiguration Config(Dictionary<string,string?> values)=>new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static void Enable(Dictionary<string,string?> s) {
        var o=SePayTestData.Options;s["SePay:Enabled"]="true";s["SePay:MerchantId"]=o.MerchantId;s["SePay:MerchantSecretKey"]=o.MerchantSecretKey;s["SePay:IpnSecretKey"]=o.IpnSecretKey;
        s["SePay:PublicOrigin"]=s["PaymentStaging:PublicOrigin"];s["SePay:ReturnUrl"]=s["PaymentStaging:PublicOrigin"]+"/api/payments/sepay/return";
        s["PaymentStaging:FrontendOrigin"]="https://web.sepay-staging.example.test";s["SePay:FrontendResultUrl"]=s["PaymentStaging:FrontendOrigin"]+"/payment/result";
        foreach(var key in new[]{"SePayMerchantVerified","SePayCallbacksVerified","SePayReconciliationPlanVerified"})s["PaymentStaging:"+key]="true";
    }
    [Fact] public void Dedicated_profile_accepts_only_new_staging_identity_and_empty_frontend_while_disabled() {
        var s=Settings();Assert.Contains("bonbon_payment_staging",PaymentStaging.ValidateConfiguration(Config(s)));
        Assert.Empty(SePayStaging.FrontendOrigins(Config(s)));Assert.Equal(SePayStaging.Database,PaymentStaging.ConfiguredDatabase(Config(s)));
    }
    [Fact] public void Separate_trusted_frontend_is_allowed_without_inheriting_production_CORS() {
        var s=Settings();Enable(s);PaymentStaging.ValidateConfiguration(Config(s));
        Assert.Equal(new[]{"https://web.sepay-staging.example.test"},SePayStaging.FrontendOrigins(Config(s)));
        s["Cors:AllowedOrigins:0"]="https://bonbon.kitsuracloud.com";
        Assert.DoesNotContain("https://bonbon.kitsuracloud.com",SePayStaging.FrontendOrigins(Config(s)));
    }
    [Theory] [InlineData("Database=bonbon")] [InlineData("Database=bonbon_payment_local")] [InlineData("Database=bonbon_payment_test")]
    [InlineData("Username=bonbon_app")] [InlineData("Username=postgres")] [InlineData("Username=bonbon_payment_test_runner")]
    [InlineData("Host=elsewhere.invalid")] [InlineData("Port=55432")] [InlineData("Pooling=false")] [InlineData("Maximum Pool Size=10")]
    [InlineData("Log Parameters=true")] [InlineData("Include Error Detail=true")] [InlineData("No Reset On Close=true")]
    [InlineData("Timeout=0")] [InlineData("Command Timeout=0")]
    public void Production_local_or_unbounded_connections_are_rejected_before_open(string change) {
        var s=Settings();s["ConnectionStrings:DefaultConnection"]+=";"+change;Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(s)));
    }
    [Theory] [InlineData("Payment:Enabled","true")] [InlineData("PaymentLocal:IpnSimulationEnabled","true")]
    [InlineData("PaymentStaging:DatabaseMarker","")] [InlineData("PaymentStaging:ExpectedDatabaseAddress","127.0.0.1")]
    [InlineData("PaymentStaging:TrustedProxyAddresses:0","0.0.0.0")] [InlineData("PaymentStaging:TrustedProxyAddresses:0","127.0.0.1")]
    [InlineData("PaymentStaging:FrontendOrigin","https://bonbon.kitsuracloud.com")]
    [InlineData("PaymentStaging:FrontendOrigin","https://web.example.test/payment/result")]
    [InlineData("PaymentStaging:FrontendOrigin","https://web.example.test?redirect=other")]
    [InlineData("AllowedHosts","*")] [InlineData("Jwt:Issuer","BonBon.API")]
    public void Unsafe_staging_configurations_fail_closed(string key,string value) {
        var s=Settings();s[key]=value;Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(s)));
    }
    [Theory] [InlineData("SePay:IpnSecretKey","")] [InlineData("SePay:ReturnUrl","")] [InlineData("SePay:FrontendResultUrl","")]
    [InlineData("PaymentStaging:FrontendOrigin","")] [InlineData("PaymentStaging:SePayMerchantVerified","false")]
    [InlineData("PaymentStaging:SePayCallbacksVerified","false")] [InlineData("PaymentStaging:SePayReconciliationPlanVerified","false")]
    [InlineData("SePay:FrontendResultUrl","https://evil.example.test/payment/result")]
    public void Enabled_cannot_bypass_missing_callbacks_keys_or_evidence(string key,string value) {
        var s=Settings();Enable(s);s[key]=value;Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(s)));
    }

    private static TestServer Server(bool enabled) {
        var o=SePayTestData.Options;o.Enabled=enabled;
        var gateway=new Mock<ISePayGateway>();gateway.SetupGet(x=>x.IsConfigured).Returns(true);
        var repo=new Mock<ISePayPaymentRepository>(MockBehavior.Strict);
        repo.Setup(x=>x.RecordRejectedAsync(It.IsAny<string>(),It.IsAny<string>(),It.IsAny<DateTimeOffset>(),It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var reconciliation=new Mock<ISePayReconciliationService>(MockBehavior.Strict);
        return new TestServer(new WebHostBuilder().ConfigureServices(s=>{
            s.AddControllers().AddApplicationPart(typeof(SePayController).Assembly);s.AddAuthorization();
            s.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
            s.AddSingleton(Options.Create(o));s.AddSingleton(gateway.Object);s.AddSingleton(repo.Object);s.AddSingleton(reconciliation.Object);
            s.AddSingleton<TimeProvider>(TimeProvider.System);s.AddScoped<ISePayIpnHandler,SePayIpnHandler>();
            s.AddCors(c=>c.AddPolicy("Frontend",p=>p.WithOrigins("https://web.sepay-test.example").AllowAnyMethod().AllowAnyHeader().AllowCredentials()));
        }).Configure(app=>{
            // TestServer-only remote-address setup. Never part of deployed API.
            app.Use((context,next)=>{context.Connection.RemoteIpAddress=IPAddress.Parse(context.Request.Headers["X-Test-Remote"]=="untrusted"?"192.0.2.99":"172.30.91.2");return next();});
            var f=new ForwardedHeadersOptions{ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto,ForwardLimit=1,RequireHeaderSymmetry=true};
            f.KnownNetworks.Clear();f.KnownProxies.Clear();f.KnownProxies.Add(IPAddress.Parse("172.30.91.2"));app.UseForwardedHeaders(f);
            app.UseRouting();app.UseCors("Frontend");app.UseAuthentication();app.UseAuthorization();
            app.UseEndpoints(e=>{e.MapControllers();e.MapGet("/test/scheme",context=>context.Response.WriteAsync(context.Request.Scheme));});
        }));
    }
    [Theory] [InlineData(false,503)] [InlineData(true,401)]
    public async Task Anonymous_IPN_reaches_real_handler_but_cannot_confirm_without_secret(bool enabled,int expected) {
        using var server=Server(enabled);using var client=server.CreateClient();
        using var response=await client.PostAsJsonAsync("/api/payments/sepay/ipn",new{timestamp=1,notification_type="CONNECTIVITY_PROBE",order=(object?)null,transaction=(object?)null});
        Assert.Equal(expected,(int)response.StatusCode);Assert.Equal("ipn-v1",response.Headers.GetValues("X-BonBon-SePay-Endpoint").Single());
    }
    [Fact] public async Task Separate_frontend_CORS_preflight_is_exact_and_production_is_denied() {
        using var server=Server(false);using var client=server.CreateClient();
        foreach(var origin in new[]{"https://web.sepay-test.example","https://bonbon.kitsuracloud.com","https://attacker.example"}) {
            using var r=new HttpRequestMessage(HttpMethod.Options,"/api/payments");r.Headers.Add("Origin",origin);r.Headers.Add("Access-Control-Request-Method","POST");r.Headers.Add("Access-Control-Request-Headers","authorization,idempotency-key,content-type");
            using var response=await client.SendAsync(r);
            Assert.Equal(origin=="https://web.sepay-test.example",response.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }
    [Theory] [InlineData(false,"https")] [InlineData(true,"http")]
    public async Task Forwarded_proto_is_only_trusted_from_the_exact_proxy(bool untrusted,string expected) {
        using var server=Server(false);using var client=server.CreateClient();using var r=new HttpRequestMessage(HttpMethod.Get,"/test/scheme");
        if(untrusted)r.Headers.Add("X-Test-Remote","untrusted");r.Headers.Add("X-Forwarded-For","198.51.100.1");r.Headers.Add("X-Forwarded-Proto","https");
        using var response=await client.SendAsync(r);Assert.Equal(expected,await response.Content.ReadAsStringAsync());
    }
    [Fact] public async Task Return_uses_fixed_frontend_and_checkout_still_requires_JWT() {
        using var server=Server(false);using var client=server.CreateClient();
        using var response=await client.GetAsync("/api/payments/sepay/return?paymentId="+Guid.NewGuid()+"&redirect=https://evil.example&status=Succeeded");
        Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);Assert.Equal("web.sepay-test.example",response.Headers.Location!.Host);
        Assert.Equal("/payment/result",response.Headers.Location.AbsolutePath);Assert.DoesNotContain("Succeeded",response.Headers.Location.Query);
        using var create=await client.PostAsJsonAsync("/api/payments",new{bookingId=Guid.NewGuid(),provider="SEPAY_SANDBOX"});Assert.Equal(HttpStatusCode.Unauthorized,create.StatusCode);
        var limit=typeof(SePayController).GetMethod(nameof(SePayController.Ipn))!.GetCustomAttributes(typeof(RequestSizeLimitAttribute),false).Cast<RequestSizeLimitAttribute>().Single();Assert.Equal(32768,((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
    }
}
