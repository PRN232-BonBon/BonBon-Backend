using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.Extensions.Options;

namespace BonBon.Business.Services.Implementations;

public sealed class SePayGateway(HttpClient http, IOptions<SePayOptions> config, ISePaySignatureService signatures) : ISePayGateway, IDisposable
{
    public bool IsConfigured => config.Value.IsValid();
    public string MerchantBinding => "sepay:form:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.Value.MerchantId))).ToLowerInvariant();
    public SePayForm BuildForm(PaymentAttempt p)
    {
        if (!IsConfigured || p.Currency != "VND" || p.Amount <= 0 || decimal.Truncate(p.Amount) != p.Amount || !Invoice(p.Reference))
            throw new ArgumentException("Invalid SePay checkout configuration or snapshot.");
        var callback = config.Value.ReturnUrl + "?paymentId=" + p.PaymentId.ToString("D");
        var fields = new Dictionary<string, string> {
            ["order_amount"] = p.Amount.ToString("0", CultureInfo.InvariantCulture), ["merchant"] = config.Value.MerchantId,
            ["currency"] = "VND", ["operation"] = "PURCHASE", ["order_description"] = "BonBon booking " + p.BookingId.ToString("N"),
            ["order_invoice_number"] = p.Reference, ["success_url"] = callback, ["error_url"] = callback, ["cancel_url"] = callback
        };
        var ordered = SePaySignatureService.SignedFields.Where(fields.ContainsKey).Select(k => new SePayField(k, fields[k])).ToList();
        ordered.Add(new("lang", "vi")); ordered.Add(new("signature", signatures.Sign(fields, config.Value.MerchantSecretKey)));
        return new(config.Value.SandboxCheckoutUrl, "POST", ordered);
    }
    public static bool Invoice(string? value) => value is not null && Regex.IsMatch(value, "\\A[a-f0-9]{32}\\z");
    public async Task<SePayQueryResult> QueryAsync(string invoice, CancellationToken ct)
    {
        if (!IsConfigured || !Invoice(invoice)) return new(null, "CONFIGURATION_INVALID");
        using var request = new HttpRequestMessage(HttpMethod.Get, config.Value.SandboxApiBaseUrl + "/v1/order/detail/" + invoice);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Value.MerchantId + ":" + config.Value.MerchantSecretKey)));
        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return new(null, ((int)response.StatusCode) switch {401 or 403 => "MERCHANT_AUTH_FAILED", 429 => "RATE_LIMITED", 404 => "ORDER_NOT_FOUND", _ => "GATEWAY_UNAVAILABLE"});
            // Bounded stream: do not buffer arbitrary provider bodies, nor log card/customer data.
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream(); var bytes = new byte[4096]; int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0) {
                if (buffer.Length + read > 262144) return new(null, "INVALID_RESPONSE");
                await buffer.WriteAsync(bytes.AsMemory(0, read), timeout.Token);
            }
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 20 });
            if (!json.RootElement.TryGetProperty("data", out var data)) return new(null, "INVALID_RESPONSE");
            var order = data.Deserialize<SePayOrder>();
            return order is null ? new(null, "INVALID_RESPONSE") : new(order);
        } catch (OperationCanceledException) { return new(null, "QUERY_UNCERTAIN"); }
          catch (HttpRequestException) { return new(null, "QUERY_UNCERTAIN"); }
          catch (JsonException) { return new(null, "INVALID_RESPONSE"); }
    }
    public void Dispose() => http.Dispose();
}
