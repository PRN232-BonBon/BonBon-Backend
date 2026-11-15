using System.Net;

namespace BonBon.Business.Configuration;

public sealed class SePayOptions
{
    public const string CheckoutEndpoint = "https://pay-sandbox.sepay.vn/v1/checkout/init";
    public const string ApiEndpoint = "https://pgapi-sandbox.sepay.vn";
    public bool Enabled { get; set; }
    public string MerchantId { get; set; } = "";
    public string MerchantSecretKey { get; set; } = "";
    public string IpnSecretKey { get; set; } = "";
    public string SandboxCheckoutUrl { get; set; } = CheckoutEndpoint;
    public string SandboxApiBaseUrl { get; set; } = ApiEndpoint;
    public string PublicOrigin { get; set; } = "";
    public string ReturnUrl { get; set; } = "";
    public string FrontendResultUrl { get; set; } = "";

    public bool IsValid() => SandboxCheckoutUrl == CheckoutEndpoint && SandboxApiBaseUrl == ApiEndpoint
        && MerchantId is { Length: >= 1 and <= 100 } && MerchantId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
        && Secret(MerchantSecretKey) && MerchantSecretKey.StartsWith("spsk_test_", StringComparison.Ordinal)
        && Secret(IpnSecretKey) && IpnSecretKey != MerchantSecretKey
        && PublicHttps(PublicOrigin, out var origin) && origin.AbsolutePath == "/"
        && PublicHttps(ReturnUrl, out var callback) && callback.GetLeftPart(UriPartial.Authority) == origin.GetLeftPart(UriPartial.Authority)
        && callback.AbsolutePath == "/api/payments/sepay/return"
        && PublicHttps(FrontendResultUrl, out var frontend) && frontend.AbsolutePath == "/payment/result";

    public static bool PublicHttps(string value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || u.Scheme != "https" || !u.IsDefaultPort
            || u.IsLoopback || u.UserInfo != "" || u.Query != "" || u.Fragment != "" || u.HostNameType != UriHostNameType.Dns
            || !u.Host.Contains('.') || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        uri = u; return true;
    }
    private static bool Secret(string value) => value is { Length: >= 24 and <= 256 } && value == value.Trim()
        && !value.Any(char.IsControl) && !value.Contains(':');
}
