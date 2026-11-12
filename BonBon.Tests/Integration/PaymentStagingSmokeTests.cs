using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BonBon.API.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit.Abstractions;

namespace BonBon.Tests.Integration;

public sealed class PaymentStagingSmokeFactAttribute : FactAttribute
{
    public PaymentStagingSmokeFactAttribute()
    { if(Environment.GetEnvironmentVariable("BONBON_PAYMENT_STAGING_PREPARE")!="I_CONFIRM_LOCAL_STAGE_TEST_DB_ONLY") Skip="Explicit local staging preparation consent is absent; no HTTP or database connection."; }
}

public class PaymentStagingSmokeTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);
    private sealed class Accounts
    {
        public Guid RunId { get; set; }=Guid.NewGuid();
        public string CustomerEmail { get; set; }="";
        public string OwnerEmail { get; set; }="";
        public string CustomerPassword { get; set; }="";
        public string OwnerPassword { get; set; }="";
        public Guid CustomerId { get; set; }
        public Guid OwnerId { get; set; }
        public Guid VehicleId { get; set; }=Guid.NewGuid();
        public Guid BookingId { get; set; }
    }
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response,HttpStatusCode expected)
    { Assert.Equal(expected,response.StatusCode); using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection,string sql,params (string,object)[] values)
    { await using var command=new NpgsqlCommand(sql,connection); foreach(var (key,value) in values) command.Parameters.AddWithValue(key,value); return (T)(await command.ExecuteScalarAsync())!; }
    private static async Task ExecuteAsync(NpgsqlConnection connection,string sql,params (string,object)[] values)
    { await using var command=new NpgsqlCommand(sql,connection); foreach(var (key,value) in values) command.Parameters.AddWithValue(key,value); await command.ExecuteNonQueryAsync(); }

    [PaymentStagingSmokeFact]
    public async Task Prepare_real_login_accounts_booking_and_verify_disabled_payment_over_local_http()
    {
        var path=Environment.GetEnvironmentVariable("BONBON_PAYMENT_STAGING_CONFIG")!;
        Assert.True(Path.IsPathFullyQualified(path));
        var config=PaymentStaging.AddLocalConfiguration(new ConfigurationBuilder(),path).Build();
        await using var source=PaymentStaging.CreateDataSource(config);
        await PaymentStaging.VerifySchemaAsync(source);
        await using var connection=await source.OpenConnectionAsync(); // Restricted test role only.
        Assert.False(config.GetValue<bool>("Payment:Enabled"));
        using var client=new HttpClient(new HttpClientHandler { AllowAutoRedirect=false }) {BaseAddress=new Uri("http://127.0.0.1:5288"),Timeout=TimeSpan.FromSeconds(30)};
        var health=await JsonAsync(await client.GetAsync("/health"),HttpStatusCode.OK);
        Assert.Equal("PaymentStaging",health.GetProperty("environment").GetString());
        Assert.Equal(PaymentStaging.Database,health.GetProperty("database").GetString()); Assert.True(health.GetProperty("databaseVerified").GetBoolean());
        Assert.False(health.GetProperty("paymentEnabled").GetBoolean());

        var accountsPath=Path.Combine(Path.GetDirectoryName(path)!,".payment-staging-accounts.local.json");
        var accounts=File.Exists(accountsPath)?JsonSerializer.Deserialize<Accounts>(await File.ReadAllTextAsync(accountsPath),JsonOptions)!:new Accounts();
        if(accounts.CustomerEmail=="")
        {
            accounts.CustomerEmail=$"stage-{accounts.RunId:N}-customer@test.invalid"; accounts.OwnerEmail=$"stage-{accounts.RunId:N}-owner@test.invalid";
            accounts.CustomerPassword=Convert.ToHexString(RandomNumberGenerator.GetBytes(24))+"Aa9!";
            accounts.OwnerPassword=Convert.ToHexString(RandomNumberGenerator.GetBytes(24))+"Aa9!";
        }
        async Task SaveAsync()=>await File.WriteAllTextAsync(accountsPath,JsonSerializer.Serialize(accounts,JsonOptions));
        await SaveAsync(); // Recoverable credentials before any account registration; never tokens.
        async Task<Guid> EnsureAccountAsync(string email,string password,string name)
        {
            Assert.StartsWith("stage-",email); Assert.EndsWith("@test.invalid",email);
            var existing=await ScalarAsync<object>(connection,"SELECT account_id FROM bonbon.account WHERE email=@email",("email",email));
            Guid id;
            if(existing is Guid found) id=found;
            else
            {
                var registered=await JsonAsync(await client.PostAsJsonAsync("/api/auth/register",new {email,password,fullName=name,deviceInfo="Payment staging preparation"}),HttpStatusCode.Created);
                id=registered.GetProperty("account").GetProperty("accountId").GetGuid();
            }
            await ExecuteAsync(connection,"INSERT INTO public.payment_test_manifest VALUES(@run,'account',@id) ON CONFLICT DO NOTHING",("run",accounts.RunId),("id",id));
            return id;
        }
        accounts.CustomerId=await EnsureAccountAsync(accounts.CustomerEmail,accounts.CustomerPassword,"Synthetic staging customer");
        accounts.OwnerId=await EnsureAccountAsync(accounts.OwnerEmail,accounts.OwnerPassword,"Synthetic staging owner");
        await SaveAsync();
        // No public role escalation endpoint is invented. Test-only owner membership
        // and vehicle are provisioned here, after the physical DB identity guard.
        await ExecuteAsync(connection,"INSERT INTO bonbon.account_role(account_id,role_id,status) VALUES(@owner,3,'Active') ON CONFLICT DO NOTHING",("owner",accounts.OwnerId));
        async Task<string> LoginAsync(string email,string password,string role)
        {
            var login=await JsonAsync(await client.PostAsJsonAsync("/api/auth/login",new {email,password,deviceInfo="Payment staging smoke"}),HttpStatusCode.OK);
            Assert.Contains(login.GetProperty("account").GetProperty("roles").EnumerateArray(),x=>x.GetString()==role);
            return login.GetProperty("accessToken").GetString()!; // In memory only.
        }
        var customerToken=await LoginAsync(accounts.CustomerEmail,accounts.CustomerPassword,"Customer");
        var ownerToken=await LoginAsync(accounts.OwnerEmail,accounts.OwnerPassword,"VehicleOwner");
        await ExecuteAsync(connection,"""
            INSERT INTO bonbon.vehicle(vehicle_id,owner_account_id,plate_number,brand,model,seats,transmission,pickup_address,hour_price,day_price,status)
            VALUES(@id,@owner,@plate,'Staging','Synthetic car',4,'Automatic','Local staging test only',100000,650000,'Available') ON CONFLICT DO NOTHING;
            INSERT INTO public.payment_test_manifest VALUES(@run,'vehicle',@id) ON CONFLICT DO NOTHING;
            """,("id",accounts.VehicleId),("owner",accounts.OwnerId),("plate","STAGE-"+accounts.VehicleId.ToString("N")[..20]),("run",accounts.RunId));
        Assert.Equal(accounts.OwnerId,await ScalarAsync<Guid>(connection,"SELECT owner_account_id FROM bonbon.vehicle WHERE vehicle_id=@id",("id",accounts.VehicleId)));
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",customerToken);
        if(accounts.BookingId==Guid.Empty)
        {
            var existing=await ScalarAsync<object>(connection,"SELECT booking_id FROM bonbon.booking WHERE vehicle_id=@vehicle AND customer_account_id=@customer ORDER BY created_at LIMIT 1",("vehicle",accounts.VehicleId),("customer",accounts.CustomerId));
            if(existing is Guid bookingId) accounts.BookingId=bookingId;
            else
            {
                var start=new DateTimeOffset(DateTime.UtcNow.Date.AddDays(14).AddHours(2),TimeSpan.Zero);
                var request=new {vehicleId=accounts.VehicleId,rentalType="Daily",startAt=start,endAt=start.AddDays(2)};
                var quote=await JsonAsync(await client.PostAsJsonAsync("/api/bookings/quote",request),HttpStatusCode.OK);
                Assert.Equal(1300000m,quote.GetProperty("quotedRentalAmount").GetDecimal());
                var booking=await JsonAsync(await client.PostAsJsonAsync("/api/bookings",request),HttpStatusCode.Created);
                accounts.BookingId=booking.GetProperty("bookingId").GetGuid(); Assert.Equal("PendingApproval",booking.GetProperty("status").GetString());
            }
            await SaveAsync();
        }
        await ExecuteAsync(connection,"INSERT INTO public.payment_test_manifest VALUES(@run,'booking',@id) ON CONFLICT DO NOTHING",("run",accounts.RunId),("id",accounts.BookingId));
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsync($"/api/bookings/{accounts.BookingId}/approve",null)).StatusCode);
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",ownerToken);
        var approved=await JsonAsync(await client.PostAsync($"/api/bookings/{accounts.BookingId}/approve",null),HttpStatusCode.OK);
        Assert.Equal("AwaitingPayment",approved.GetProperty("status").GetString()); Assert.Equal(1300000m,approved.GetProperty("quotedRentalAmount").GetDecimal());
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",customerToken);
        var mine=await JsonAsync(await client.GetAsync("/api/bookings/mine"),HttpStatusCode.OK);
        Assert.Contains(mine.EnumerateArray(),x=>x.GetProperty("bookingId").GetGuid()==accounts.BookingId && x.GetProperty("status").GetString()=="AwaitingPayment");
        client.DefaultRequestHeaders.Add("Idempotency-Key","staging_preparation_disabled_0001");
        var unavailable=await JsonAsync(await client.PostAsJsonAsync("/api/payments",new {bookingId=accounts.BookingId}),HttpStatusCode.ServiceUnavailable);
        Assert.Equal("PAYMENT_DISABLED",unavailable.GetProperty("title").GetString());
        Assert.Equal(0L,await ScalarAsync<long>(connection,"SELECT count(*) FROM bonbon.payment WHERE booking_id=@id",("id",accounts.BookingId)));
        client.DefaultRequestHeaders.Authorization=null;
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/bookings/mine")).StatusCode);
        using(var preflight=new HttpRequestMessage(HttpMethod.Options,"/api/bookings/mine"))
        {
            preflight.Headers.Add("Origin","http://127.0.0.1:5289"); preflight.Headers.Add("Access-Control-Request-Method","GET"); preflight.Headers.Add("Access-Control-Request-Headers","authorization");
            using var response=await client.SendAsync(preflight); Assert.Equal("http://127.0.0.1:5289",response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }
        output.WriteLine($"Local real HTTP Auth/Booking smoke passed; Customer={accounts.CustomerId}, Owner={accounts.OwnerId}, Vehicle={accounts.VehicleId}, Booking={accounts.BookingId}, amount=1300000 VND, AwaitingPayment. Payment=disabled, attempts=0. Credentials retained in ignored local file; no tokens logged.");
    }
}
