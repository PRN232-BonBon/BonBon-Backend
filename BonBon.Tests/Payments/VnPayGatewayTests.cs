using System.Net;
using System.Security.Cryptography;
using System.Text;
using BonBon.Business.Configuration;
using BonBon.Business.Services.Implementations;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace BonBon.Tests.Payments;

public class VnPayGatewayTests
{
    private static VnPayGateway Gateway(VnPayOptions? options = null) => new(Options.Create(options ?? PaymentTestData.GatewayOptions));

    private static Dictionary<string, string> KnownCallback() => new()
    {
        ["vnp_Amount"] = "130000000", ["vnp_OrderInfo"] = "BonBon rental test", ["vnp_PayDate"] = "20261023090000",
        ["vnp_ResponseCode"] = "00", ["vnp_TmnCode"] = "TEST0001", ["vnp_TransactionNo"] = "12345678",
        ["vnp_TransactionStatus"] = "00", ["vnp_TxnRef"] = "ABC123",
        // Independently calculated with Node crypto HMAC-SHA512, sorted form-encoded fields.
        ["vnp_SecureHash"] = "95fe75122083b87b8cb00b1340d3fd3d037a9d8952aad15f22727857733ed96798e6fa81bb63991ec543a550cbd8d3b086232423db16f6b5d2fe4f4522aec9d8",
    };

    private static void Sign(Dictionary<string, string> parameters)
    {
        var canonical = string.Join("&", parameters.Where(x => x.Key != "vnp_SecureHash" && x.Value != "")
            .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{WebUtility.UrlEncode(x.Key)}={WebUtility.UrlEncode(x.Value)}"));
        parameters["vnp_SecureHash"] = Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(PaymentTestData.TestSecret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    [Fact]
    public void Verifies_independent_signature_vector_and_vietnam_payment_time()
    {
        var result = Gateway().Verify(KnownCallback());
        Assert.True(result.IsValid);
        Assert.Equal(PaymentTestData.Now, result.Notification!.PaidAt);
        Assert.Equal(130000000, result.Notification.AmountMinor);
    }

    [Fact]
    public void Creates_sandbox_url_with_vnd_multiplier_and_expiry()
    {
        var payment = PaymentTestData.Attempt;
        var uri = new Uri(Gateway().CreateCheckoutUrl(payment, "127.0.0.1"));
        Assert.Equal("sandbox.vnpayment.vn", uri.Host);
        var query = QueryHelpers.ParseQuery(uri.Query).ToDictionary(x => x.Key, x => x.Value.ToString());
        Assert.Equal("130000000", query["vnp_Amount"]);
        Assert.Equal("20261023085800", query["vnp_CreateDate"]);
        Assert.Equal("20261023091300", query["vnp_ExpireDate"]);
        Assert.Equal(payment.Reference, query["vnp_TxnRef"]);
        Assert.Equal(PaymentTestData.GatewayOptions.ReturnUrl, query["vnp_ReturnUrl"]);
        var signature = query["vnp_SecureHash"];
        Sign(query);
        Assert.Equal(signature, query["vnp_SecureHash"]);
        Assert.DoesNotContain(PaymentTestData.TestSecret, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("vnp_Amount", "100")]
    [InlineData("vnp_TxnRef", "OTHER")]
    [InlineData("vnp_ResponseCode", "24")]
    [InlineData("vnp_TmnCode", "OTHER001")]
    public void Rejects_unsigned_tampering(string field, string value)
    {
        var query = KnownCallback();
        query[field] = value;
        Assert.Equal("INVALID_SIGNATURE", Gateway().Verify(query).ErrorCode);
    }

    [Theory]
    [InlineData("vnp_TmnCode", "OTHER001", "INVALID_MERCHANT")]
    [InlineData("vnp_TxnRef", "bad ref", "INVALID_REFERENCE")]
    [InlineData("vnp_Amount", "130000001", "INVALID_AMOUNT")]
    [InlineData("vnp_Amount", "-100", "INVALID_AMOUNT")]
    [InlineData("vnp_Amount", "1000000000000", "INVALID_AMOUNT")]
    [InlineData("vnp_Amount", "0", "INVALID_AMOUNT")]
    [InlineData("vnp_TransactionStatus", "x", "INVALID_RESULT")]
    [InlineData("vnp_TransactionNo", "0", "INVALID_RESULT")]
    [InlineData("vnp_TransactionNo", "000", "INVALID_REFERENCE")]
    [InlineData("vnp_PayDate", "20260230090000", "INVALID_RESULT")]
    [InlineData("vnp_PayDate", "00010101000000", "INVALID_RESULT")]
    public void Rejects_signed_but_invalid_fields(string field, string value, string error)
    {
        var query = KnownCallback();
        query[field] = value;
        Sign(query);
        Assert.Equal(error, Gateway().Verify(query).ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    [InlineData(10000000000)]
    public void Rejects_amounts_outside_gateway_limits(double amount) =>
        Assert.Throws<ArgumentException>(() => Gateway().CreateCheckoutUrl(PaymentTestData.Attempt with { Amount = (decimal)amount }, "127.0.0.1"));

    [Fact]
    public void Refuses_production_gateway_and_missing_secret()
    {
        var production = PaymentTestData.GatewayOptions;
        production.PaymentUrl = "https://pay.vnpay.vn/vpcpay.html";
        Assert.False(Gateway(production).IsConfigured);
        Assert.Throws<InvalidOperationException>(() => Gateway(production).CreateCheckoutUrl(PaymentTestData.Attempt, "127.0.0.1"));
        var empty = PaymentTestData.GatewayOptions;
        empty.HashSecret = "";
        Assert.False(Gateway(empty).IsConfigured);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("AA")]
    [InlineData("95fe75122083b87b8cb00b1340d3fd3d037a9d8952aad15f22727857733ed96798e6fa81bb63991ec543a550cbd8d3b086232423db16f6b5d2fe4f4522aec9d8\n")]
    public void Rejects_malformed_signature(string signature)
    {
        var query = KnownCallback();
        query["vnp_SecureHash"] = signature;
        Assert.Equal("INVALID_SIGNATURE", Gateway().Verify(query).ErrorCode);
    }
}
