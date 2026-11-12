using BonBon.API.Configuration;
using BonBon.Business.Configuration;
using Microsoft.Extensions.Configuration;

namespace BonBon.Tests.Payments;

public sealed class PaymentStagingMerchantLocalFactAttribute : FactAttribute
{
    public PaymentStagingMerchantLocalFactAttribute()
    {
        if(Environment.GetEnvironmentVariable("BONBON_PAYMENT_MERCHANT_LOCAL_CHECK")!="READ_CONFIG_ONLY")
            Skip="Explicit local config-only check absent; no secret file is opened.";
    }
}

public class PaymentStagingMerchantLocalTests
{
    [PaymentStagingMerchantLocalFact]
    public void Merchant_credentials_bind_from_local_file_without_enabling_payment()
    {
        // Never use assertions that echo actual configuration values, exception details,
        // hashes or signatures. No DB, HTTP, gateway checkout or payment attempt.
        try
        {
            var path=Environment.GetEnvironmentVariable("BONBON_PAYMENT_STAGING_CONFIG");
            Assert.True(path is not null && Path.IsPathFullyQualified(path),"Local config path: CHUA DAT.");
            var file=PaymentStaging.AddLocalConfiguration(new ConfigurationBuilder(),path!).Build();
            var effective=PaymentStaging.AddLocalConfiguration(new ConfigurationBuilder(),path!).AddEnvironmentVariables().Build();
            var gateway=effective.GetSection("VnPay").Get<VnPayOptions>()??new();
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(gateway.TmnCode,"\\A[A-Za-z0-9]{8}\\z"),"TmnCode format: CHUA DAT.");
            Assert.True(!string.IsNullOrWhiteSpace(gateway.HashSecret) && gateway.HashSecret.Length>=16
                && gateway.HashSecret==gateway.HashSecret.Trim(),"HashSecret format: CHUA DAT.");
            Assert.True(gateway.TmnCode==file["VnPay:TmnCode"] && gateway.HashSecret==file["VnPay:HashSecret"],
                "Environment overrides local merchant credentials: CHUA DAT.");
            Assert.True(gateway.PaymentUrl==VnPayOptions.SandboxUrl,"Sandbox gateway: CHUA DAT.");
            Assert.True(!effective.GetValue<bool>("Payment:Enabled"),"Payment disabled: CHUA DAT.");
            _=PaymentStaging.ValidateConfiguration(effective); // Validates test target, never opens a connection.
        }
        catch(Xunit.Sdk.XunitException) { throw; }
        catch { Assert.Fail("Local configuration load/validation: CHUA DAT. Details suppressed to protect secrets."); }
    }
}
