using System.Text.Json.Serialization;

namespace BonBon.Business.DTOs.Payments;

public sealed record PaymentCheckoutResponse(Guid PaymentId, Guid BookingId, decimal Amount, string Currency,
    string Status, string? CheckoutUrl, DateTimeOffset ExpiresAt, bool IsReplay);
public sealed record PaymentStatusResponse(Guid PaymentId, Guid BookingId, decimal Amount, string Currency,
    string Status, DateTimeOffset ExpiresAt, bool RequiresReconciliation);
public sealed record PaymentReturnResponse(Guid PaymentId, string Status, bool RequiresReconciliation, string Message);
public sealed record VnPayIpnResponse(
    [property: JsonPropertyName("RspCode")] string RspCode,
    [property: JsonPropertyName("Message")] string Message);
