using BonBon.API.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BonBon.Tests.Payments;

public class PaymentStagingSafetyTests
{
    private static Dictionary<string,string?> Settings()=>new() {
        ["ConnectionStrings:DefaultConnection"]="Host=db.example.test;Database=bonbon_payment_test;Username=bonbon_payment_test_runner;Password=synthetic-unit-test-only;Pooling=true;Maximum Pool Size=2;Minimum Pool Size=0",
        ["PaymentStaging:ExpectedDatabaseHost"]="db.example.test",
        ["Jwt:Key"]=new string('X',64),["Jwt:Issuer"]="BonBon.PaymentStaging",["Jwt:Audience"]="BonBon.PaymentStaging.Web",["Payment:Enabled"]="false" };
    private static IConfiguration Config(Dictionary<string,string?> settings)=>new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    [Fact] public void Disabled_stage_accepts_only_fixed_test_target_without_merchant()
    { Assert.Equal(PaymentStaging.Database,new NpgsqlConnectionStringBuilder(PaymentStaging.ValidateConfiguration(Config(Settings()))).Database); }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("db.example.test,other.example.test")]
    [InlineData("https://db.example.test")]
    [InlineData(" db.example.test")]
    public void Missing_or_broad_host_pin_cannot_fall_back_to_the_connection_string(string? pin)
    { var settings=Settings(); settings["PaymentStaging:ExpectedDatabaseHost"]=pin; Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(settings))); }
    [Theory]
    [InlineData("Database=bonbon")]
    [InlineData("Username=bonbon_app")]
    [InlineData("Host=other.invalid")]
    [InlineData("Maximum Pool Size=4")]
    [InlineData("Pooling=false")]
    [InlineData("Log Parameters=true")]
    [InlineData("Include Error Detail=true")]
    [InlineData("No Reset On Close=true")]
    public void Unsafe_connection_is_rejected_before_opening(string change)
    { var settings=Settings(); settings["ConnectionStrings:DefaultConnection"]+=";"+change; Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(settings))); }
    [Fact] public void Enabling_payment_without_real_merchant_and_https_readiness_fails_closed()
    { var settings=Settings(); settings["Payment:Enabled"]="true"; Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(settings))); }
    [Fact] public void Production_jwt_identity_is_rejected()
    { var settings=Settings(); settings["Jwt:Issuer"]="BonBon"; Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(settings))); }

    private static Dictionary<string,string?> HttpsSettings(string origin="https://payment-stage.example.net")
    {
        var settings=Settings();
        settings["PaymentStaging:PublicOrigin"]=origin;
        settings["VnPay:ReturnUrl"]=origin+"/api/payments/vnpay/return/redirect";
        settings["PaymentStaging:IpnUrl"]=origin+"/api/payments/vnpay/ipn";
        settings["PaymentReturn:FrontendResultUrl"]=origin+"/payment/result";
        settings["AllowedHosts"]=new Uri(origin).Host+";localhost;127.0.0.1";
        return settings;
    }
    [Fact] public void Prepared_https_can_be_validated_without_enabling_payment_or_merchant()
    { PaymentStaging.ValidateConfiguration(Config(HttpsSettings())); }
    [Theory]
    [InlineData("https://bonbon.kitsuracloud.com")]
    [InlineData("https://stage.bonbon.kitsuracloud.com")]
    [InlineData("http://payment-stage.example.net")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://payment-stage.example.net:4443")]
    [InlineData("https://user@payment-stage.example.net")]
    public void Unsafe_public_origin_is_rejected_even_with_payment_off(string origin)
    { Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(HttpsSettings(origin)))); }
    [Theory]
    [InlineData("VnPay:ReturnUrl","https://attacker.invalid/return")]
    [InlineData("PaymentReturn:FrontendResultUrl","http://127.0.0.1:5289/payment/result")]
    [InlineData("PaymentStaging:IpnUrl","https://other.invalid/api/payments/vnpay/ipn")]
    [InlineData("AllowedHosts","*")]
    [InlineData("AllowedHosts","payment-stage.example.net;bonbon.kitsuracloud.com")]
    public void Mixed_callbacks_or_broad_host_filter_fail_closed(string key,string value)
    { var settings=HttpsSettings(); settings[key]=value; Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(settings))); }
    [Fact] public void Merchant_and_https_flags_cannot_bypass_missing_reconciliation_plan()
    {
        var settings=HttpsSettings(); settings["Payment:Enabled"]="true";
        settings["VnPay:TmnCode"]="UNITTEST"; settings["VnPay:HashSecret"]="synthetic-unit-test-only";
        settings["PaymentStaging:MerchantVerified"]="true"; settings["PaymentStaging:CallbacksVerified"]="true";
        Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(settings)));
        settings["PaymentStaging:ReconciliationPlanVerified"]="true";
        PaymentStaging.ValidateConfiguration(Config(settings)); // Unit format check, not real merchant verification.
    }
}
