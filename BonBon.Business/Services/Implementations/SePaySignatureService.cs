using System.Security.Cryptography;
using System.Text;
using BonBon.Business.Services.Interfaces;

namespace BonBon.Business.Services.Implementations;

public sealed class SePaySignatureService : ISePaySignatureService
{
    public static readonly IReadOnlyList<string> SignedFields = Array.AsReadOnly(new[] { "order_amount", "merchant", "currency", "operation",
        "order_description", "order_invoice_number", "customer_id", "payment_method", "success_url", "error_url", "cancel_url" });
    public string Sign(IReadOnlyDictionary<string, string> fields, string secret)
    {
        var text = string.Join(',', SignedFields.Where(k => fields.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) && v != "0")
            .Select(k => k + "=" + fields[k]));
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text)));
    }
    public bool Verify(IReadOnlyDictionary<string, string> fields, string secret, string signature)
    {
        if (signature is null || signature.Length != 44) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(Sign(fields, secret)), Convert.FromBase64String(signature)); }
        catch (FormatException) { return false; }
    }
}
