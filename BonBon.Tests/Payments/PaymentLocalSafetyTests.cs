using BonBon.API.Configuration;
using Microsoft.Extensions.Configuration;

namespace BonBon.Tests.Payments;

public class PaymentLocalSafetyTests
{
    private static Dictionary<string,string?> Settings() => new()
    {
        ["ConnectionStrings:DefaultConnection"]="Host=127.0.0.1;Port=55432;Database=bonbon_payment_local;Username=bonbon_payment_local_runner;Password=synthetic-test-only;MaxPoolSize=2;MinPoolSize=0",
        ["Jwt:Issuer"]="BonBon.PaymentLocal", ["Jwt:Audience"]="BonBon.PaymentLocal.Web", ["Jwt:Key"]=new('x',64),
        ["AllowedHosts"]="127.0.0.1;localhost", ["Payment:Enabled"]="true",
        ["VnPay:TmnCode"]="TEST1234", ["VnPay:HashSecret"]="synthetic-test-only",
        ["VnPay:ReturnUrl"]="https://127.0.0.1:5287/api/payments/vnpay/return/redirect",
        ["PaymentReturn:FrontendResultUrl"]="http://127.0.0.1:5289/payment/result"
    };
    [Fact] public void Accepts_only_local_checkout_configuration_without_connecting()
        => Assert.NotEmpty(PaymentLocal.ValidateConfiguration(new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build()));

    [Theory]
    [InlineData("Host=192.0.2.1;Port=55432;Database=bonbon_payment_local;Username=bonbon_payment_local_runner")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=bonbon_payment_local;Username=bonbon_payment_local_runner")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=bonbon;Username=bonbon_payment_local_runner")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=bonbon_payment_local;Username=postgres")]
    public void Rejects_other_hosts_ports_databases_and_admin_role(string connection)
    {
        var values=Settings(); values["ConnectionStrings:DefaultConnection"]=connection+";Password=synthetic-test-only;MaxPoolSize=2";
        Assert.Throws<InvalidOperationException>(()=>PaymentLocal.ValidateConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }
    [Theory]
    [InlineData("VnPay:HashSecret", "")]
    [InlineData("VnPay:ReturnUrl", "https://example.test/callback")]
    [InlineData("PaymentReturn:FrontendResultUrl", "https://example.test/payment/result")]
    [InlineData("Kestrel:Endpoints:Public:Url", "http://0.0.0.0:5288")]
    public void Rejects_invalid_merchant_or_nonlocal_endpoints(string key,string value)
    {
        var values=Settings(); values[key]=value;
        Assert.Throws<InvalidOperationException>(()=>PaymentLocal.ValidateConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }
}
