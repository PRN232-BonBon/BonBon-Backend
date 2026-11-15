using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BonBon.Business.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BonBon.API.Configuration;

// No route is registered outside PaymentLocal. This tool NEVER writes SQL states.
public static class LocalVnPayIpnSimulation
{
    public const string Warning = "GIẢ LẬP VNPay IPN — KHÔNG PHẢI KẾT QUẢ TỪ VNPay";
    public sealed record Request(Guid PaymentId, string Scenario);
    public sealed record Snapshot(Guid PaymentId, Guid BookingId, string Reference, decimal Amount,
        string PaymentStatus, string BookingStatus, bool Reconciliation, long AuditCount, DateTimeOffset ExpiresAt);
    private sealed record Saved(string Scenario, string Query);
    private static readonly ConcurrentDictionary<Guid,Saved> Payloads = new();
    private static readonly SemaphoreSlim Gate = new(1,1);
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect=false, UseProxy=false }) { Timeout=TimeSpan.FromSeconds(10) };

    public static bool CanRegister(string environment, bool enabled) => environment == PaymentLocal.EnvironmentName && enabled;

    public static string BuildQuery(VnPayOptions options, string reference, decimal amount, string scenario,
        string transaction, DateTimeOffset now)
    {
        if(!options.IsValid() || !new[]{"success","failure","cancel","bad-signature","wrong-amount"}.Contains(scenario))
            throw new ArgumentException("Invalid simulation configuration/scenario.");
        var fields=new Dictionary<string,string>
        {
            ["vnp_TmnCode"]=options.TmnCode, ["vnp_TxnRef"]=reference,
            ["vnp_Amount"]=checked((long)(amount*100)+(scenario=="wrong-amount"?100:0)).ToString(CultureInfo.InvariantCulture),
            ["vnp_OrderInfo"]="LOCAL SIMULATION BonBon "+reference, ["vnp_BankCode"]="NCB", ["vnp_CardType"]="ATM",
            ["vnp_ResponseCode"]=scenario=="failure"?"51":scenario=="cancel"?"24":"00",
            ["vnp_TransactionStatus"]=scenario is "failure" or "cancel"?"02":"00",
            ["vnp_TransactionNo"]=transaction,
            ["vnp_PayDate"]=now.ToOffset(TimeSpan.FromHours(7)).ToString("yyyyMMddHHmmss",CultureInfo.InvariantCulture)
        };
        // VNPay 2.1.0: ordinal sort, form URL-encoding and HMAC-SHA512. No new gateway.
        var canonical=string.Join("&",fields.OrderBy(x=>x.Key,StringComparer.Ordinal)
            .Select(x=>$"{WebUtility.UrlEncode(x.Key)}={WebUtility.UrlEncode(x.Value)}"));
        var signature=Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(options.HashSecret),Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return canonical+"&vnp_SecureHash="+(scenario=="bad-signature"?new string('0',128):signature);
    }

    public static void MapLocalVnPayIpnSimulation(this WebApplication app)
    {
        if(!CanRegister(app.Environment.EnvironmentName,app.Configuration.GetValue<bool>("PaymentLocal:IpnSimulationEnabled"))) return;
        app.MapPost("/api/local/vnpay-ipn-simulation", async (Request request, HttpContext context, NpgsqlDataSource source,
            IOptions<VnPayOptions> options, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl="no-store";
            if(context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
                || context.Request.Headers.Origin != "http://127.0.0.1:5289"
                || context.Request.Headers["X-BonBon-Local-Simulation"] != "I_UNDERSTAND_LOCAL_SIMULATION") return Results.StatusCode(403);
            if(!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier),out var customer)) return Results.Unauthorized();
            if(!new[]{"success","failure","cancel","duplicate","bad-signature","wrong-amount"}.Contains(request.Scenario)) return Results.BadRequest(new {warning=Warning,error="UNKNOWN_SCENARIO"});
            await PaymentLocal.VerifySchemaAsync(source,ct); // Also verifies physical DB identity.
            await Gate.WaitAsync(ct);
            try
            {
                var before=await ReadAsync(source,request.PaymentId,customer,ct);
                if(before is null) return Results.NotFound(new {warning=Warning,error="OWNED_SYNTHETIC_PAYMENT_REQUIRED"});
                string query;
                if(request.Scenario=="duplicate")
                {
                    if(!Payloads.TryGetValue(request.PaymentId,out var previous) || previous.Scenario!="success")
                        return Results.Conflict(new {warning=Warning,error="CLICK_SUCCESS_FIRST_ON_A_DEDICATED_DUPLICATE_CASE"});
                    query=previous.Query; // Exact replay, including transaction number, timestamp and signature.
                }
                else if(Payloads.TryGetValue(request.PaymentId,out var previous))
                {
                    if(previous.Scenario!=request.Scenario) return Results.Conflict(new {warning=Warning,error="USE_A_NEW_BOOKING_FOR_EACH_SCENARIO"});
                    query=previous.Query; // Browser/network retry never constructs a different callback.
                }
                else
                {
                    if(before.PaymentStatus!="Pending" || before.BookingStatus!="AwaitingPayment" || before.Reconciliation || before.ExpiresAt<=DateTimeOffset.UtcNow)
                        return Results.Conflict(new {warning=Warning,error="FRESH_PAYABLE_ATTEMPT_REQUIRED"});
                    if(Payloads.Count>=1000) return Results.Conflict(new {warning=Warning,error="LOCAL_SIMULATION_LIMIT"});
                    var transaction="990"+RandomNumberGenerator.GetInt32(100000000,999999999).ToString(CultureInfo.InvariantCulture);
                    query=BuildQuery(options.Value,before.Reference,before.Amount,request.Scenario,transaction,DateTimeOffset.UtcNow);
                    Payloads[request.PaymentId]=new(request.Scenario,query);
                }
                // Fixed loopback destination, no redirects/proxy; never send merchant/signature to an arbitrary URL.
                using var response=await Client.GetAsync("http://127.0.0.1:5288/api/payments/vnpay/ipn?"+query,ct);
                var ipn=await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken:ct);
                var code=ipn.TryGetProperty("RspCode",out var value)?value.GetString():null;
                var after=await ReadAsync(source,request.PaymentId,customer,ct);
                return Results.Ok(new {warning=Warning,simulated=true,scenario=request.Scenario,ipnHttpStatus=(int)response.StatusCode,rspCode=code,before,after});
            }
            catch(HttpRequestException) { return Results.Problem(statusCode:503,title:"LOCAL_IPN_TRANSPORT_FAILED",detail:Warning+". Kiểm tra trạng thái trước khi retry cùng thao tác."); }
            finally { Gate.Release(); }
        }).RequireAuthorization(policy=>policy.RequireRole("Customer"));
    }

    private static async Task<Snapshot?> ReadAsync(NpgsqlDataSource source,Guid payment,Guid customer,CancellationToken ct)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT p.payment_id,p.booking_id,p.provider_reference,p.amount,p.status,b.status,p.needs_reconciliation,
              (SELECT count(*) FROM bonbon.payment_notification n WHERE n.payment_id=p.payment_id),p.expires_at
            FROM bonbon.payment p JOIN bonbon.booking b USING(booking_id)
            JOIN bonbon.vehicle v USING(vehicle_id) JOIN bonbon.account a ON a.account_id=b.customer_account_id
            JOIN bonbon.account o ON o.account_id=v.owner_account_id
            WHERE p.payment_id=@payment AND b.customer_account_id=@customer AND p.provider='VNPAY_SANDBOX'
              AND a.email LIKE '%-local@test.invalid' AND o.email LIKE '%-local@test.invalid'
              AND v.plate_number LIKE 'LOCAL-%' AND bonbon.has_active_role(a.account_id,2::smallint)
              AND bonbon.has_active_role(o.account_id,3::smallint)
            """,c);
        cmd.Parameters.AddWithValue("payment",payment);cmd.Parameters.AddWithValue("customer",customer);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)?new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetDecimal(3),r.GetString(4),r.GetString(5),r.GetBoolean(6),r.GetInt64(7),r.GetFieldValue<DateTimeOffset>(8)):null;
    }
}
