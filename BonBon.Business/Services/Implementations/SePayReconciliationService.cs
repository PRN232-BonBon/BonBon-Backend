using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BonBon.Business.Services.Implementations;

public sealed class SePayReconciliationService(ISePayPaymentRepository repository, ISePayGateway gateway,
    IOptions<SePayOptions> config, TimeProvider clock) : ISePayReconciliationService
{
    public Task<int> ReconcileAsync(string invoice, CancellationToken ct) => ApplyAsync(invoice, null, ct);
    public async Task<int> ApplyAsync(string invoice, SePayIpn? ipn, CancellationToken ct)
    {
        if (!config.Value.Enabled || !gateway.IsConfigured) return 503;
        if (!SePayGateway.Invoice(invoice)) return 400;
        try {
            var p = await repository.Payments.GetByReferenceAsync(invoice, ct);
            if (p is null) return await Reject("UNKNOWN_INVOICE", invoice, 404, ct);
            if (p.CheckoutUrl != gateway.MerchantBinding) return await Reject("MERCHANT_CHANGED", invoice, 409, ct);
            // Authenticated REST response is merchant-scoped. Never trust browser/customer result codes.
            var query = await gateway.QueryAsync(invoice, ct);
            if (query.Order is not { } o) return await Reject(query.Error ?? "QUERY_UNCERTAIN", invoice, 503, ct);
            if (o.Invoice != invoice || !Guid.TryParse(o.Id, out var orderUuid) || orderUuid == Guid.Empty || o.OrderId is not { Length: >= 1 and <= 100 }
                || (o.Merchant is not null && o.Merchant != config.Value.MerchantId)) return await Reject("ORDER_MISMATCH", invoice, 400, ct);
            if (o.Currency != "VND" || !Money(o.Amount, out var amount) || amount != p.Amount) return await Reject("ORDER_AMOUNT_CURRENCY_MISMATCH", invoice, 400, ct);
            if (ipn is not null && (ipn.Order is not { } incoming || incoming.Id != o.Id || incoming.OrderId != o.OrderId
                || incoming.Invoice != invoice || incoming.Currency != o.Currency || incoming.Status != o.Status
                || !Money(incoming.Amount, out var ia) || ia != amount)) return await Reject("IPN_ORDER_MISMATCH", invoice, 400, ct);
            var transactions = o.Transactions ?? [];
            if (transactions.Any(t => t is null)) return await Reject("INVALID_TRANSACTION_DATA", invoice, 400, ct);
            // More than one approved payment is an anomaly; do not silently select one.
            if (transactions.Count(t => t.Type == "PAYMENT" && t.Status == "APPROVED") > 1) return await ApplyUncertain(p, o, "MULTIPLE_PROVIDER_TRANSACTIONS", ct);
            var t = ipn is null ? transactions.SingleOrDefault(t => t.Type == "PAYMENT" && t.Status == "APPROVED") : transactions.SingleOrDefault(t => t.Id == ipn.Transaction?.Id);
            if (ipn is null && t is null && o.Status == "CANCELLED")
                return await ApplyUncertain(p, o, "CANCELLED_ORDER_REQUIRES_REVIEW", ct); // No proof of a terminal transaction.
            if (t is null) return await Reject("TRANSACTION_NOT_VERIFIED", invoice, 409, ct);
            if (!Guid.TryParse(t.Id, out var txn) || txn == Guid.Empty || (t.Type != "PAYMENT" && !(ipn?.NotificationType == "TRANSACTION_VOID" && t.Type == "VOID")) || t.Currency != "VND"
                || !Money(t.Amount, out var ta) || ta != amount) return await Reject("TRANSACTION_MISMATCH", invoice, 400, ct);
            if (ipn?.Transaction is { } it && (it.Type != t.Type || it.Status != t.Status || it.Currency != t.Currency
                || !Money(it.Amount, out var ipa) || ipa != amount || it.Date != t.Date)) return await Reject("IPN_TRANSACTION_MISMATCH", invoice, 400, ct);
            var success = o.Status == "CAPTURED" && t.Status == "APPROVED" && (ipn is null || ipn.NotificationType == "ORDER_PAID");
            // VOID/reversal evidence is an anomaly, not permission to charge the customer again.
            if (!success) return await ApplyUncertain(p, o, "SEPAY_RESULT_UNCERTAIN", ct);
            if (!DateTime.TryParseExact(t.Date, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return await Reject("INVALID_TRANSACTION_TIME", invoice, 400, ct);
            var paid = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromHours(7)).ToUniversalTime();
            var evidence = "order=" + o.Id + ";orderStatus=" + o.Status + ";transaction=" + txn.ToString("D") + ";transactionStatus=" + t.Status + ";source=MERCHANT_REST";
            var n = new VerifiedPaymentNotification(invoice, checked((long)(amount * 100)), "00", "00", txn.ToString("D"), paid,
                Hash(invoice + "|" + evidence), SePayEvidence: evidence);
            var outcome = await repository.Payments.ProcessNotificationAsync(n, clock.GetUtcNow(), c => PaymentNotificationPolicy.Decide(c, n), ct);
            return outcome is PaymentNotificationOutcome.Applied or PaymentNotificationOutcome.Duplicate ? 200 : outcome == PaymentNotificationOutcome.InvalidAmount ? 400 : 404;
        } catch (PaymentSchemaUnavailableException) { return 503; }
          catch (NpgsqlException) { return 503; }
          catch (InvalidOperationException) { return 503; }
    }
    private async Task<int> ApplyUncertain(PaymentAttempt p, SePayOrder o, string reason, CancellationToken ct)
    {
        var evidence = "order=" + o.Id + ";orderStatus=" + o.Status + ";source=MERCHANT_REST;reason=" + reason;
        var n = new VerifiedPaymentNotification(p.Reference, checked((long)(p.Amount * 100)), "99", "99", null, null,
            Hash(p.Reference + "|" + evidence), SePayEvidence: evidence);
        await repository.Payments.ProcessNotificationAsync(n, clock.GetUtcNow(), c => new(PaymentNotificationAction.ReconcilePending, reason), ct);
        return 200;
    }
    private async Task<int> Reject(string reason, string invoice, int status, CancellationToken ct) {
        await repository.RecordRejectedAsync(reason, Hash(reason + "|" + invoice), clock.GetUtcNow(), ct); return status;
    }
    public static bool Money(string? value, out decimal amount) => decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
        && amount > 0 && amount <= 9999999999m && decimal.Truncate(amount) == amount;
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
