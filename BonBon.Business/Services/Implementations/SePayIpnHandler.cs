using System.Security.Cryptography;
using System.Text;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BonBon.Business.Services.Implementations;

public sealed class SePayIpnHandler(IOptions<SePayOptions> config, ISePayGateway gateway,
    ISePayReconciliationService reconciliation, ISePayPaymentRepository repository, TimeProvider clock) : ISePayIpnHandler
{
    public async Task<int> HandleAsync(string secret, SePayIpn ipn, CancellationToken ct)
    {
        if (!config.Value.Enabled || !gateway.IsConfigured) return 503;
        // Fixed-size digest comparison; a missing key can never authenticate an IPN.
        var valid = !string.IsNullOrEmpty(secret) && secret.Length <= 256 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(secret)), SHA256.HashData(Encoding.UTF8.GetBytes(config.Value.IpnSecretKey)));
        var reason = !valid ? "IPN_AUTH_FAILED"
            : ipn.NotificationType is not ("ORDER_PAID" or "TRANSACTION_VOID") ? "IPN_TYPE_INVALID"
            : ipn.Order is null || ipn.Transaction is null || !SePayGateway.Invoice(ipn.Order.Invoice) ? "IPN_PAYLOAD_INVALID"
            : ipn.Timestamp <= 0 || ipn.Timestamp > clock.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds() ? "IPN_TIMESTAMP_INVALID" : null;
        if (reason is not null) {
            try { await repository.RecordRejectedAsync(reason, SePayReconciliationService.Hash(reason), clock.GetUtcNow(), ct); }
            catch (NpgsqlException) { return 503; } // No unbounded untrusted payload is stored.
            return valid ? 400 : 401;
        }
        // Old deliveries are re-queried rather than trusted or blindly rejected; stable evidence hashes deduplicate retries.
        return await reconciliation.ApplyAsync(ipn.Order!.Invoice, ipn, ct);
    }
}
