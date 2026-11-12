namespace BonBon.Business.Configuration;

public sealed class PaymentReturnOptions
{
    // Deployment configuration only. Never take a destination from callback parameters.
    public string FrontendResultUrl { get; set; } = "";

    public bool TryGetDestination(out Uri destination)
    {
        destination = null!;
        if (!Uri.TryCreate(FrontendResultUrl, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
            uri.AbsolutePath != "/payment/result" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
            return false;
        destination = uri;
        return true;
    }
}
