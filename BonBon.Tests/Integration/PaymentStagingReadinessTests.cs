using System.Net;
using System.Text.Json;
using BonBon.API.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BonBon.Tests.Integration;

public sealed class PaymentStagingReadOnlyFactAttribute : FactAttribute
{
    public PaymentStagingReadOnlyFactAttribute()
    {
        if(Environment.GetEnvironmentVariable("BONBON_PAYMENT_STAGING_READ_ONLY")!="I_CONFIRM_LOCAL_READ_ONLY")
            Skip="Local staging read-only probe requires explicit opt-in; no connection made.";
    }
}

public class PaymentStagingReadinessTests
{
    [PaymentStagingReadOnlyFact]
    public async Task Local_stage_identity_return_spa_cors_and_attempt_count_are_safe()
    {
        var path=Environment.GetEnvironmentVariable("BONBON_PAYMENT_STAGING_CONFIG")!;
        Assert.True(Path.IsPathFullyQualified(path));
        var config=PaymentStaging.AddLocalConfiguration(new ConfigurationBuilder(),path).AddEnvironmentVariables().Build();
        Assert.False(config.GetValue<bool>("Payment:Enabled"));
        Assert.True(string.IsNullOrWhiteSpace(config["PaymentStaging:PublicOrigin"])); // Local-only phase.
        await using var source=PaymentStaging.CreateDataSource(config);
        await PaymentStaging.VerifySchemaAsync(source);
        await using var connection=await source.OpenConnectionAsync();
        await using var transaction=await connection.BeginTransactionAsync();
        await using(var guard=new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='5000ms'; SET LOCAL lock_timeout='1000ms'",connection,transaction))
            await guard.ExecuteNonQueryAsync();
        using var accounts=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!,".payment-staging-accounts.local.json")));
        var bookingId=accounts.RootElement.GetProperty("bookingId").GetGuid();
        async Task AssertBookingAsync()
        {
            await using var command=new NpgsqlCommand("""
                SELECT b.status,b.quoted_rental_amount,(SELECT count(*) FROM bonbon.payment p WHERE p.booking_id=b.booking_id)
                FROM bonbon.booking b WHERE b.booking_id=@id
                """,connection,transaction);
            command.Parameters.AddWithValue("id",bookingId);
            await using var reader=await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); Assert.Equal("AwaitingPayment",reader.GetString(0));
            Assert.Equal(1300000m,reader.GetDecimal(1)); Assert.Equal(0L,reader.GetInt64(2));
        }
        await AssertBookingAsync();
        using var client=new HttpClient(new HttpClientHandler {AllowAutoRedirect=false}) {Timeout=TimeSpan.FromSeconds(15)};
        foreach(var origin in new[]{"http://127.0.0.1:5288","http://127.0.0.1:5289"})
        {
            using var health=await client.GetAsync(origin+"/health"); Assert.Equal(HttpStatusCode.OK,health.StatusCode);
            using var body=JsonDocument.Parse(await health.Content.ReadAsStringAsync());
            Assert.Equal(PaymentStaging.Database,body.RootElement.GetProperty("database").GetString());
            Assert.True(body.RootElement.GetProperty("databaseVerified").GetBoolean());
            Assert.False(body.RootElement.GetProperty("paymentEnabled").GetBoolean());
            using var redirected=await client.GetAsync(origin+"/api/payments/vnpay/return/redirect?redirectUrl=https%3A%2F%2Fattacker.invalid&vnp_ResponseCode=00");
            Assert.Equal(HttpStatusCode.Found,redirected.StatusCode);
            Assert.Equal("http://127.0.0.1:5289/payment/result?returnError=unavailable",redirected.Headers.Location!.AbsoluteUri);
            Assert.Contains("no-referrer",redirected.Headers.GetValues("Referrer-Policy"));
            using var unauthorized=await client.GetAsync(origin+"/api/payments/"+Guid.NewGuid());
            Assert.Equal(HttpStatusCode.Unauthorized,unauthorized.StatusCode);
            using var ipn=await client.GetAsync(origin+"/api/payments/vnpay/ipn?vnp_ResponseCode=00");
            using var ipnBody=JsonDocument.Parse(await ipn.Content.ReadAsStringAsync());
            Assert.Equal("99",ipnBody.RootElement.GetProperty("RspCode").GetString());
        }
        foreach(var origin in new[]{"http://127.0.0.1:5289","https://attacker.invalid"})
        {
            using var request=new HttpRequestMessage(HttpMethod.Options,"http://127.0.0.1:5288/api/bookings/mine");
            request.Headers.Add("Origin",origin); request.Headers.Add("Access-Control-Request-Method","GET");
            request.Headers.Add("Access-Control-Request-Headers","authorization");
            using var response=await client.SendAsync(request);
            if(origin.EndsWith(":5289")) Assert.Equal(origin,response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            else Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        foreach(var suffix in new[]{"/payment/result","/payment/result?paymentId=modified&status=Succeeded"})
        {
            using var page=await client.GetAsync("http://127.0.0.1:5289"+suffix);
            Assert.Equal(HttpStatusCode.OK,page.StatusCode);
            Assert.Equal("text/html",page.Content.Headers.ContentType!.MediaType);
        }
        using var swagger=await client.GetAsync("http://127.0.0.1:5288/swagger/index.html");
        Assert.Equal(HttpStatusCode.NotFound,swagger.StatusCode);
        await AssertBookingAsync();
        await transaction.RollbackAsync();
    }
}
