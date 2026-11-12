namespace BonBon.Business.Configuration;

public sealed class VnPayOptions
{
    public const string SandboxUrl = "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html";
    public string PaymentUrl { get; set; } = SandboxUrl;
    public string TmnCode { get; set; } = "";
    public string HashSecret { get; set; } = "";
    public string ReturnUrl { get; set; } = "";

    public bool IsValid() => PaymentUrl == SandboxUrl &&
        !string.IsNullOrEmpty(TmnCode) && System.Text.RegularExpressions.Regex.IsMatch(TmnCode, "\\A[A-Za-z0-9]{8}\\z") &&
        !string.IsNullOrWhiteSpace(HashSecret) && HashSecret.Length >= 16 &&
        Uri.TryCreate(ReturnUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}
