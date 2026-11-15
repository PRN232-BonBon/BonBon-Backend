using System.Text.Json.Serialization;

namespace BonBon.Business.DTOs.Payments;

public sealed record SePayField(string Name, string Value);
public sealed record SePayForm(string Action, string Method, IReadOnlyList<SePayField> Fields);
public sealed record SePayCheckoutResponse(Guid PaymentId, Guid BookingId, string Provider, decimal Amount,
    string Currency, string Status, DateTimeOffset ExpiresAt, bool IsReplay, SePayForm? Checkout);
public sealed record SePayIpn(
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("notification_type")] string NotificationType,
    [property: JsonPropertyName("order")] SePayOrder? Order,
    [property: JsonPropertyName("transaction")] SePayTransaction? Transaction);
public sealed record SePayOrder(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("order_id")] string OrderId,
    [property: JsonPropertyName("order_invoice_number")] string Invoice,
    [property: JsonPropertyName("order_status")] string Status,
    [property: JsonPropertyName("order_amount")] string Amount,
    [property: JsonPropertyName("order_currency")] string Currency,
    [property: JsonPropertyName("transactions")] SePayTransaction[]? Transactions = null,
    [property: JsonPropertyName("merchant")] string? Merchant = null);
public sealed record SePayTransaction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("transaction_type")] string Type,
    [property: JsonPropertyName("transaction_status")] string Status,
    [property: JsonPropertyName("transaction_amount")] string Amount,
    [property: JsonPropertyName("transaction_currency")] string Currency,
    [property: JsonPropertyName("transaction_date")] string Date);
public sealed record SePayQueryResult(SePayOrder? Order, string? Error = null);
