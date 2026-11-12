using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BonBon.Business.Configuration;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.Extensions.Options;

namespace BonBon.Business.Services.Implementations;

public sealed class VnPayGateway(IOptions<VnPayOptions> options) : IPaymentGateway
{
    private readonly VnPayOptions _options = options.Value;
    public bool IsConfigured => _options.IsValid();

    public string CreateCheckoutUrl(PaymentAttempt payment, string ipAddress)
    {
        if (!IsConfigured) throw new InvalidOperationException("VNPay sandbox configuration is invalid.");
        if (!IsValidAmount(payment.Amount) || payment.Currency != "VND" ||
            !Regex.IsMatch(payment.Reference, "\\A[A-Za-z0-9]{1,100}\\z") ||
            !IPAddress.TryParse(ipAddress, out _) || payment.ExpiresAt <= payment.CreatedAt)
            throw new ArgumentException("Invalid checkout data.");
        var parameters = new Dictionary<string, string>
        {
            ["vnp_Version"] = "2.1.0", ["vnp_Command"] = "pay", ["vnp_TmnCode"] = _options.TmnCode,
            ["vnp_Amount"] = checked((long)(payment.Amount * 100)).ToString(CultureInfo.InvariantCulture),
            ["vnp_CurrCode"] = "VND", ["vnp_TxnRef"] = payment.Reference,
            ["vnp_OrderInfo"] = $"BonBon rental {payment.Reference}", ["vnp_OrderType"] = "other",
            ["vnp_Locale"] = "vn", ["vnp_ReturnUrl"] = _options.ReturnUrl, ["vnp_IpAddr"] = ipAddress,
            ["vnp_CreateDate"] = FormatDate(payment.CreatedAt), ["vnp_ExpireDate"] = FormatDate(payment.ExpiresAt),
        };
        var canonical = Canonicalize(parameters);
        return $"{_options.PaymentUrl}?{canonical}&vnp_SecureHash={Sign(canonical)}";
    }

    public PaymentVerification Verify(IReadOnlyDictionary<string, string> parameters)
    {
        if (!IsConfigured) return new(false, "PAYMENT_CONFIGURATION_INVALID");
        if (parameters.Count > 40 || parameters.Any(x => x.Key.Length > 100 || x.Value.Length > 1024) ||
            !parameters.TryGetValue("vnp_SecureHash", out var signature) || !Regex.IsMatch(signature, "\\A[a-fA-F0-9]{128}\\z"))
            return new(false, "INVALID_SIGNATURE");
        var canonical = Canonicalize(parameters);
        var expected = Convert.FromHexString(Sign(canonical));
        if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature)))
            return new(false, "INVALID_SIGNATURE");
        string Value(string key) => parameters.TryGetValue(key, out var value) ? value : "";
        if (Value("vnp_TmnCode") != _options.TmnCode) return new(false, "INVALID_MERCHANT");
        if (!Regex.IsMatch(Value("vnp_TxnRef"), "\\A[A-Za-z0-9]{1,100}\\z")) return new(false, "INVALID_REFERENCE");
        var rawAmount = Value("vnp_Amount");
        if (!Regex.IsMatch(rawAmount, "\\A[0-9]{1,12}\\z") ||
            !long.TryParse(rawAmount, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0 || amount % 100 != 0)
            return new(false, "INVALID_AMOUNT");
        var responseCode = Value("vnp_ResponseCode");
        var status = Value("vnp_TransactionStatus");
        if (!Regex.IsMatch(responseCode, "\\A[0-9]{2}\\z") || !Regex.IsMatch(status, "\\A[0-9]{2}\\z"))
            return new(false, "INVALID_RESULT");
        var transactionNo = Value("vnp_TransactionNo");
        if (transactionNo is "" or "0") transactionNo = null;
        else if (!Regex.IsMatch(transactionNo, "\\A[0-9]{1,15}\\z") ||
                 !ulong.TryParse(transactionNo, NumberStyles.None, CultureInfo.InvariantCulture, out var numericTransaction) || numericTransaction == 0)
            return new(false, "INVALID_REFERENCE");
        DateTimeOffset? paidAt = null;
        var rawDate = Value("vnp_PayDate");
        if (rawDate != "")
        {
            if (!DateTime.TryParseExact(rawDate, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return new(false, "INVALID_RESULT");
            try { paidAt = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromHours(7)).ToUniversalTime(); }
            catch (ArgumentOutOfRangeException) { return new(false, "INVALID_RESULT"); }
        }
        if (responseCode == "00" && status == "00" && (transactionNo is null || paidAt is null))
            return new(false, "INVALID_RESULT");
        return new(true, Notification: new(Value("vnp_TxnRef"), amount, responseCode, status,
            transactionNo, paidAt, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))));
    }

    public static bool IsValidAmount(decimal amount) => amount > 0 && amount <= 9999999999m && decimal.Truncate(amount) == amount;
    // VNPay's .NET sample signs form URL-encoded values (space => '+'), sorted ordinally.
    private static string Canonicalize(IReadOnlyDictionary<string, string> parameters) => string.Join("&", parameters
        .Where(x => x.Key.StartsWith("vnp_", StringComparison.Ordinal) && x.Key is not ("vnp_SecureHash" or "vnp_SecureHashType") && x.Value != "")
        .OrderBy(x => x.Key, StringComparer.Ordinal)
        .Select(x => $"{WebUtility.UrlEncode(x.Key)}={WebUtility.UrlEncode(x.Value)}"));
    private string Sign(string canonical) => Convert.ToHexString(HMACSHA512.HashData(
        Encoding.UTF8.GetBytes(_options.HashSecret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    private static string FormatDate(DateTimeOffset date) => date.ToOffset(TimeSpan.FromHours(7)).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
}
