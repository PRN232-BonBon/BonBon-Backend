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
    [Fact] public void Staging_cannot_accept_a_local_simulator_flag_even_when_payment_is_off()
    { var settings=Settings();settings["PaymentLocal:IpnSimulationEnabled"]="true";Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(settings))); }
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("10.0.0.0/8")]
    [InlineData("*")]
    public void Stage_proxy_trust_cannot_be_broad(string address)
    {var settings=Settings();settings["PaymentStaging:TrustedProxyAddresses:0"]=address;Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(settings)));}
    [Fact]
    public void Stage_allows_an_exact_operator_approved_proxy_address()
    {var settings=Settings();settings["PaymentStaging:TrustedProxyAddresses:0"]="192.0.2.10";Assert.Single(PaymentStaging.TrustedProxyAddresses(Config(settings)));PaymentStaging.ValidateConfiguration(Config(settings));}
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
    [Fact] public void SePay_requires_provider_specific_readiness_not_legacy_verified_flags()
    {
        var s=HttpsSettings();var o=SePayTestData.Options;
        s["SePay:Enabled"]="true";s["SePay:MerchantId"]=o.MerchantId;s["SePay:MerchantSecretKey"]=o.MerchantSecretKey;s["SePay:IpnSecretKey"]=o.IpnSecretKey;
        s["SePay:PublicOrigin"]="https://payment-stage.example.net";s["SePay:ReturnUrl"]="https://payment-stage.example.net/api/payments/sepay/return";s["SePay:FrontendResultUrl"]="https://payment-stage.example.net/payment/result";
        s["PaymentStaging:MerchantVerified"]="true";s["PaymentStaging:CallbacksVerified"]="true";s["PaymentStaging:ReconciliationPlanVerified"]="true";
        Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(s)));
        s["PaymentStaging:SePayMerchantVerified"]="true";s["PaymentStaging:SePayCallbacksVerified"]="true";s["PaymentStaging:SePayReconciliationPlanVerified"]="true";
        PaymentStaging.ValidateConfiguration(Config(s));
        s["SePay:PublicOrigin"]="https://different-stage.example.net";s["SePay:ReturnUrl"]="https://different-stage.example.net/api/payments/sepay/return";
        Assert.Throws<InvalidOperationException>(()=>PaymentStaging.ValidateConfiguration(Config(s)));
    }
    [Theory]
    [InlineData("https://bonbon.kitsuracloud.com")]
    [InlineData("https://bonbon-api.kitsuracloud.com")]
    [InlineData("https://bonbon-admin.kitsuracloud.com")]
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
