using BonBon.Business.Configuration;
using Npgsql;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace BonBon.API.Configuration;

public static class PaymentStaging
{
    public const string EnvironmentName="PaymentStaging";
    public const string Database="bonbon_payment_test", Role="bonbon_payment_test_runner";
    public static string RequireExpectedDatabaseHost(string? value)
    {
        if(string.IsNullOrWhiteSpace(value) || value!=value.Trim() || value.Length>253
            || Uri.CheckHostName(value)==UriHostNameType.Unknown || value.IndexOfAny([',',';','/','\\','*'])>=0)
            throw new InvalidOperationException("An independently configured single approved database host is required.");
        return value;
    }
    public static IConfigurationBuilder AddLocalConfiguration(IConfigurationBuilder builder,string path)
    {
        if(!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("An absolute local configuration path is required.");
        // Explicit filename only: the default PhysicalFileProvider hides dotfiles.
        return builder.AddJsonFile(new PhysicalFileProvider(Path.GetDirectoryName(path)!,ExclusionFilters.None),
            Path.GetFileName(path),optional:false,reloadOnChange:false);
    }

    public static string ValidateConfiguration(IConfiguration configuration)
    {
        var raw=configuration.GetConnectionString("DefaultConnection");
        if(string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Staging requires an explicit test connection.");
        var connection=new NpgsqlConnectionStringBuilder(raw);
        var expectedHost=RequireExpectedDatabaseHost(configuration["PaymentStaging:ExpectedDatabaseHost"]);
        if(connection.Host!=expectedHost || connection.Port!=5432 || connection.Database!=Database || connection.Username!=Role
            || string.IsNullOrWhiteSpace(connection.Password) || connection.Multiplexing || connection.NoResetOnClose)
            throw new InvalidOperationException("Staging connection must target the fixed test database and restricted role.");
        if(!connection.Pooling || connection.MaxPoolSize!=2 || connection.MinPoolSize!=0 || connection.IncludeErrorDetail || connection.LogParameters)
            throw new InvalidOperationException("Staging requires a bounded pool of two and safe logging.");
        if(configuration["Jwt:Issuer"]!="BonBon.PaymentStaging" || configuration["Jwt:Audience"]!="BonBon.PaymentStaging.Web"
            || (configuration["Jwt:Key"]?.Length??0)<64)
            throw new InvalidOperationException("Staging requires its own JWT configuration.");
        var publicOrigin=configuration["PaymentStaging:PublicOrigin"];
        Uri? origin=null;
        if(!string.IsNullOrWhiteSpace(publicOrigin))
        {
            if(!Uri.TryCreate(publicOrigin,UriKind.Absolute,out origin) || origin.Scheme!="https"
                || origin.HostNameType!=UriHostNameType.Dns || origin.IsLoopback || !origin.IsDefaultPort
                || origin.UserInfo!="" || origin.AbsolutePath!="/" || origin.Query!="" || origin.Fragment!=""
                || origin.Host.Equals("bonbon.kitsuracloud.com",StringComparison.OrdinalIgnoreCase)
                || origin.Host.EndsWith(".bonbon.kitsuracloud.com",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A dedicated HTTPS staging origin is required; production is forbidden.");
            if(configuration["VnPay:ReturnUrl"]!=new Uri(origin,"/api/payments/vnpay/return/redirect").AbsoluteUri
                || configuration["PaymentReturn:FrontendResultUrl"]!=new Uri(origin,"/payment/result").AbsoluteUri
                || configuration["PaymentStaging:IpnUrl"]!=new Uri(origin,"/api/payments/vnpay/ipn").AbsoluteUri)
                throw new InvalidOperationException("Staging callback URLs must match the fixed HTTPS origin, even while Payment is disabled.");
            var hosts=(configuration["AllowedHosts"]??"").Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            if(!hosts.Contains(origin.Host,StringComparer.OrdinalIgnoreCase) || hosts.Any(x=>x.Contains('*')
                || !(x.Equals(origin.Host,StringComparison.OrdinalIgnoreCase) || x is "localhost" or "127.0.0.1")))
                throw new InvalidOperationException("Staging host filtering must explicitly allow only staging and loopback hosts.");
        }
        if(configuration.GetValue<bool>("Payment:Enabled"))
        {
            var gateway=configuration.GetSection("VnPay").Get<VnPayOptions>()??new();
            var destination=configuration.GetSection("PaymentReturn").Get<PaymentReturnOptions>()??new();
            if(!gateway.IsValid() || !destination.TryGetDestination(out var frontend) || frontend.Scheme!="https"
                || origin is null || !configuration.GetValue<bool>("PaymentStaging:CallbacksVerified")
                || !configuration.GetValue<bool>("PaymentStaging:MerchantVerified")
                || !configuration.GetValue<bool>("PaymentStaging:ReconciliationPlanVerified")
                || gateway.ReturnUrl!=new Uri(origin,"/api/payments/vnpay/return/redirect").AbsoluteUri
                || frontend.AbsoluteUri!=new Uri(origin,"/payment/result").AbsoluteUri
                || configuration["PaymentStaging:IpnUrl"]!=new Uri(origin,"/api/payments/vnpay/ipn").AbsoluteUri)
                throw new InvalidOperationException("Payment stays blocked until genuine merchant and fixed HTTPS callbacks are verified.");
        }
        return connection.ConnectionString;
    }

    public static NpgsqlDataSource CreateDataSource(IConfiguration configuration)
    {
        var builder=new NpgsqlDataSourceBuilder(ValidateConfiguration(configuration));
        // Applies to physical connections opened by EF AND raw GetDbConnection()
        // repository calls. No migration or data write is performed by this guard.
        builder.UsePhysicalConnectionInitializer(
            connection=>{ using var command=IdentityCommand(connection); using var reader=command.ExecuteReader(); Verify(reader); },
            async connection=>{ await using var command=IdentityCommand(connection); await using var reader=await command.ExecuteReaderAsync(); await VerifyAsync(reader,CancellationToken.None); });
        return builder.Build();
    }

    private static NpgsqlCommand IdentityCommand(NpgsqlConnection connection)=>new("""
        SELECT current_database(),current_user,session_user,pg_get_userbyid(d.datdba),
            shobj_description(d.oid,'pg_database'),current_setting('server_version'),
            NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole,r.rolconnlimit,
            to_regclass('bonbon.payment_gateway_schema') IS NOT NULL,
            to_regclass('bonbon.payment_notification') IS NOT NULL
        FROM pg_database d JOIN pg_roles r ON r.rolname=current_user WHERE d.datname=current_database()
        """,connection);
    private static void Verify(NpgsqlDataReader reader)
    { if(!reader.Read()) throw new InvalidOperationException("Staging identity unavailable."); Check(reader); }
    private static async Task VerifyAsync(NpgsqlDataReader reader,CancellationToken ct)
    { if(!await reader.ReadAsync(ct)) throw new InvalidOperationException("Staging identity unavailable."); Check(reader); }
    private static void Check(NpgsqlDataReader reader)
    {
        if(reader.GetString(0)!=Database || reader.GetString(1)!=Role || reader.GetString(2)!=Role || reader.GetString(3)!=Role
            || reader.IsDBNull(4) || !reader.GetString(4).StartsWith("bonbon-payment-test:v1:",StringComparison.Ordinal)
            || reader.GetString(5)!="16.15" || !reader.GetBoolean(6) || reader.GetInt32(7)!=4 || !reader.GetBoolean(8) || !reader.GetBoolean(9))
            throw new InvalidOperationException("Staging database identity/permissions/schema guard rejected the connection.");
    }

    public static async Task VerifySchemaAsync(NpgsqlDataSource source,CancellationToken ct=default)
    {
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var command=new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM bonbon.payment_gateway_schema WHERE version=1)
                AND (SELECT stage='ready' FROM public.payment_test_control WHERE singleton=1)
                AND EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='bonbon'
                    AND proname='protect_booking_quote' AND prosrc LIKE '%NEW.customer_account_id IS DISTINCT%'
                    AND prosrc LIKE '%NEW.status <> ''PendingApproval''%')
                AND (SELECT count(*)=4 FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid
                    JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='bonbon' AND i.indisunique AND i.indisvalid
                      AND i.indisready AND c.relname IN ('uq_vnpay_sandbox_active','uq_vnpay_sandbox_idempotency','uq_vnpay_sandbox_accepted_success','uq_vnpay_sandbox_transaction'))
            """,connection);
        if(await command.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Staging requires reviewed test schema 004/005.");
    }
}
