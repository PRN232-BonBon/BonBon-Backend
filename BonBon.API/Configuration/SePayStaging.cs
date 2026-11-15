using System.Net;
using BonBon.Business.Configuration;
using Npgsql;

namespace BonBon.API.Configuration;

// Opt-in profile for a NEW staging host/cluster. Does not relax legacy or desktop database guards.
public static class SePayStaging
{
    public const string Profile = "SePayDedicated";
    public const string Database = "bonbon_payment_staging";
    public const string Role = "bonbon_payment_staging_runner";
    public const string Owner = "bonbon_payment_staging_owner";
    public static bool IsSelected(IConfiguration config) => config["PaymentStaging:DatabaseProfile"] == Profile;

    public static Uri RequireOrigin(string? value)
    {
        if (!SePayOptions.PublicHttps(value ?? "", out var uri) || uri.AbsolutePath != "/"
            || uri.Host is "bonbon.kitsuracloud.com" or "bonbon-api.kitsuracloud.com" or "bonbon-admin.kitsuracloud.com"
            || uri.Host.EndsWith(".bonbon.kitsuracloud.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An explicit dedicated HTTPS staging origin is required; production origins are forbidden.");
        return uri;
    }
    public static string[] FrontendOrigins(IConfiguration config)
    {
        var value = config["PaymentStaging:FrontendOrigin"];
        return string.IsNullOrWhiteSpace(value) ? [] : [RequireOrigin(value).GetLeftPart(UriPartial.Authority)];
    }
    public static string ValidateConfiguration(IConfiguration config)
    {
        if (!IsSelected(config) || config.GetValue<bool>("Payment:Enabled")
            || config.GetValue<bool>("PaymentLocal:IpnSimulationEnabled"))
            throw new InvalidOperationException("Dedicated SePay staging forbids legacy checkout and local simulators.");
        var proxies = PaymentStaging.TrustedProxyAddresses(config);
        if (proxies.Length != 1 || IPAddress.IsLoopback(proxies[0]))
            throw new InvalidOperationException("Dedicated staging requires one exact independently configured container proxy address.");
        var host = PaymentStaging.RequireExpectedDatabaseHost(config["PaymentStaging:ExpectedDatabaseHost"]);
        if (!IPAddress.TryParse(config["PaymentStaging:ExpectedDatabaseAddress"], out var databaseAddress)
            || databaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(databaseAddress)
            || databaseAddress.Equals(IPAddress.Any)) throw new InvalidOperationException("Pin the isolated PostgreSQL server address.");
        var marker = config["PaymentStaging:DatabaseMarker"];
        if (marker is null || !marker.StartsWith("bonbon-sepay-staging:v1:", StringComparison.Ordinal)
            || !Guid.TryParseExact(marker["bonbon-sepay-staging:v1:".Length..], "D", out var markerId) || markerId == Guid.Empty)
            throw new InvalidOperationException("A unique staging database marker is required.");
        var c = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection"));
        if (c.Host != host || c.Port != 5432 || c.Database != Database || c.Username != Role || string.IsNullOrWhiteSpace(c.Password)
            || !c.Pooling || c.MaxPoolSize != 2 || c.MinPoolSize != 0 || c.Multiplexing || c.NoResetOnClose
            || c.IncludeErrorDetail || c.LogParameters || c.Timeout is < 1 or > 10 || c.CommandTimeout is < 1 or > 15)
            throw new InvalidOperationException("Dedicated staging requires its own fixed database/role and bounded safe connection settings.");
        if (config["Jwt:Issuer"] != "BonBon.SePayStaging" || config["Jwt:Audience"] != "BonBon.SePayStaging.Web"
            || (config["Jwt:Key"]?.Length ?? 0) < 64)
            throw new InvalidOperationException("Dedicated staging requires an independent JWT identity/key.");
        var origin = RequireOrigin(config["PaymentStaging:PublicOrigin"]);
        var hosts = (config["AllowedHosts"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!hosts.Contains(origin.Host, StringComparer.OrdinalIgnoreCase) || hosts.Any(h => h != "localhost" && h != "127.0.0.1"
            && !h.Equals(origin.Host, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Restrict API AllowedHosts to the staging backend and loopback.");
        var frontend = FrontendOrigins(config);
        var sepay = config.GetSection("SePay").Get<SePayOptions>() ?? new();
        if ((!string.IsNullOrEmpty(sepay.PublicOrigin) && RequireOrigin(sepay.PublicOrigin) != origin)
            || (!string.IsNullOrEmpty(sepay.ReturnUrl) && sepay.ReturnUrl != new Uri(origin, "/api/payments/sepay/return").AbsoluteUri)
            || (!string.IsNullOrEmpty(sepay.FrontendResultUrl) && (frontend.Length != 1
                || sepay.FrontendResultUrl != frontend[0] + "/payment/result")))
            throw new InvalidOperationException("Callbacks must match the fixed independently trusted backend/frontend origins.");
        if (sepay.Enabled && (!sepay.IsValid() || frontend.Length != 1
            || sepay.PublicOrigin.TrimEnd('/') != origin.GetLeftPart(UriPartial.Authority)
            || sepay.ReturnUrl != new Uri(origin, "/api/payments/sepay/return").AbsoluteUri
            || sepay.FrontendResultUrl != frontend[0] + "/payment/result"
            || !config.GetValue<bool>("PaymentStaging:SePayMerchantVerified")
            || !config.GetValue<bool>("PaymentStaging:SePayCallbacksVerified")
            || !config.GetValue<bool>("PaymentStaging:SePayReconciliationPlanVerified")))
            throw new InvalidOperationException("SePay remains disabled until merchant, callbacks, frontend and reconciliation readiness have evidence.");
        return c.ConnectionString;
    }

    public static NpgsqlDataSource CreateDataSource(IConfiguration config)
    {
        var builder = new NpgsqlDataSourceBuilder(ValidateConfiguration(config));
        builder.UsePhysicalConnectionInitializer(
            c => { using var command = IdentityCommand(c, config); if (command.ExecuteScalar() is not true) throw new InvalidOperationException("Dedicated staging database identity rejected."); },
            async c => { await using var command = IdentityCommand(c, config); if (await command.ExecuteScalarAsync() is not true) throw new InvalidOperationException("Dedicated staging database identity rejected."); });
        return builder.Build();
    }
    private static NpgsqlCommand IdentityCommand(NpgsqlConnection c, IConfiguration config)
    {
        var cmd = new NpgsqlCommand("""
            SELECT current_database()='bonbon_payment_staging' AND current_user='bonbon_payment_staging_runner'
              AND session_user=current_user AND pg_get_userbyid(d.datdba)='bonbon_payment_staging_owner'
              AND shobj_description(d.oid,'pg_database')=@marker
              AND inet_server_addr()=@address::inet AND inet_server_port()=5432
              AND current_setting('server_version_num')::int=160015
              AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolbypassrls
              AND NOT r.rolreplication AND r.rolconnlimit=4
              AND NOT has_database_privilege(current_user,current_database(),'CREATE')
              AND NOT has_schema_privilege(current_user,'bonbon','CREATE')
              AND NOT has_schema_privilege(current_user,'public','CREATE')
              AND NOT EXISTS(SELECT 1 FROM pg_roles other WHERE other.rolname<>current_user
                    AND pg_has_role(current_user,other.oid,'MEMBER'))
              AND NOT EXISTS(SELECT 1 FROM pg_database other WHERE NOT other.datistemplate
                    AND other.datname NOT IN('postgres','bonbon_payment_staging'))
              AND NOT has_database_privilege(current_user,'postgres','CONNECT')
            FROM pg_database d JOIN pg_roles r ON r.rolname=current_user WHERE d.datname=current_database()
            """, c);
        cmd.Parameters.AddWithValue("marker", config["PaymentStaging:DatabaseMarker"]!);
        cmd.Parameters.AddWithValue("address", config["PaymentStaging:ExpectedDatabaseAddress"]!);
        return cmd;
    }
    public static async Task VerifySchemaAsync(NpgsqlDataSource source, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM bonbon.payment_gateway_schema WHERE version=1)
              AND EXISTS(SELECT 1 FROM bonbon.momo_gateway_schema WHERE version=1)
              AND EXISTS(SELECT 1 FROM bonbon.sepay_gateway_schema WHERE version=1)
              AND EXISTS(SELECT 1 FROM public.payment_staging_control WHERE singleton=1 AND stage='ready')
              AND EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='bonbon'
                   AND proname='protect_booking_quote' AND prosrc LIKE '%NEW.customer_account_id IS DISTINCT%'
                   AND prosrc LIKE '%discount_amount_snapshot%' AND prosrc LIKE '%NEW.status <> ''PendingApproval''%')
              AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='bonbon.booking'::regclass AND tgname='booking_quote_guard'
                   AND NOT tgisinternal AND tgenabled IN('O','A') AND tgfoid='bonbon.protect_booking_quote()'::regprocedure)
              AND (SELECT count(*)=5 FROM information_schema.columns WHERE table_schema='bonbon' AND table_name='booking'
                   AND column_name IN('weekly_discount_percent_snapshot','discount_amount_snapshot','included_mileage_km_snapshot','overage_fee_per_km_snapshot','rental_terms_snapshot'))
              AND (SELECT count(*)=12 FROM pg_index i JOIN pg_class t ON t.oid=i.indexrelid JOIN pg_namespace n ON n.oid=t.relnamespace
                   WHERE n.nspname='bonbon' AND i.indisunique AND i.indisvalid AND i.indisready AND t.relname IN
                    ('uq_vnpay_sandbox_active','uq_vnpay_sandbox_idempotency','uq_vnpay_sandbox_accepted_success','uq_vnpay_sandbox_transaction',
                     'uq_momo_sandbox_active','uq_momo_sandbox_idempotency','uq_momo_sandbox_accepted_success','uq_momo_sandbox_transaction',
                     'uq_sepay_sandbox_active','uq_sepay_sandbox_idempotency','uq_sepay_sandbox_accepted_success','uq_sepay_sandbox_transaction'))
              AND (SELECT count(*)=2 FROM pg_index i JOIN pg_class t ON t.oid=i.indexrelid JOIN pg_namespace n ON n.oid=t.relnamespace
                   WHERE n.nspname='bonbon' AND i.indisunique AND i.indisvalid AND i.indisready
                     AND i.indnkeyatts=1 AND pg_get_indexdef(i.indexrelid,1,true)='booking_id'
                     AND t.relname IN('uq_sandbox_active_booking','uq_sandbox_accepted_booking')
                     AND pg_get_expr(i.indpred,i.indrelid) LIKE '%SEPAY_SANDBOX%'
                     AND pg_get_expr(i.indpred,i.indrelid) LIKE '%VNPAY_SANDBOX%'
                     AND pg_get_expr(i.indpred,i.indrelid) LIKE '%MOMO_SANDBOX%'
                     AND ((t.relname='uq_sandbox_active_booking' AND pg_get_expr(i.indpred,i.indrelid) LIKE '%Pending%'
                           AND pg_get_expr(i.indpred,i.indrelid) LIKE '%Processing%')
                       OR (t.relname='uq_sandbox_accepted_booking' AND pg_get_expr(i.indpred,i.indrelid) LIKE '%Succeeded%'
                           AND pg_get_expr(i.indpred,i.indrelid) LIKE '%NOT needs_reconciliation%')))
              AND to_regclass('bonbon.sepay_payment_evidence') IS NOT NULL
              AND to_regclass('bonbon.sepay_rejected_notification') IS NOT NULL
              AND EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='bonbon.payment'::regclass
                   AND conname='ck_sepay_sandbox_attempt' AND convalidated)
              AND EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='bonbon.sepay_payment_evidence'::regclass
                   AND confrelid='bonbon.payment_notification'::regclass AND contype='f' AND convalidated)
              AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='bonbon' AND table_name='payment'
                   AND column_name='provider_transaction_no' AND data_type='character varying' AND character_maximum_length=100)
            """, c);
        if (await cmd.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Dedicated staging requires reviewed 004/005/006/007 schema and a ready control marker.");
    }
}
