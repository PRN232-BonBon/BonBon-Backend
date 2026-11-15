using BonBon.Business.Configuration;
using Npgsql;

namespace BonBon.API.Configuration;

// Explicit desktop-only checkout environment. Does not relax PaymentStaging guards.
public static class PaymentLocal
{
    public const string EnvironmentName = "PaymentLocal";
    public const string Database = "bonbon_payment_local";
    public const string Role = "bonbon_payment_local_runner";
    public static string ValidateConfiguration(IConfiguration config)
    {
        var connection = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection"));
        if (connection.Host != "127.0.0.1" || connection.Port != 55432 || connection.Database != Database
            || connection.Username != Role || string.IsNullOrWhiteSpace(connection.Password)
            || !connection.Pooling || connection.MaxPoolSize != 2 || connection.MinPoolSize != 0
            || connection.IncludeErrorDetail || connection.LogParameters || connection.Multiplexing || connection.NoResetOnClose)
            throw new InvalidOperationException("PaymentLocal requires the dedicated loopback database and restricted login.");
        if (config["Jwt:Issuer"] != "BonBon.PaymentLocal" || config["Jwt:Audience"] != "BonBon.PaymentLocal.Web"
            || (config["Jwt:Key"]?.Length ?? 0) < 64 || config["AllowedHosts"] != "127.0.0.1;localhost"
            || config.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("PaymentLocal requires isolated JWT, host filtering and fixed loopback listeners.");
        var gateway = config.GetSection("VnPay").Get<VnPayOptions>() ?? new();

        if (gateway.ReturnUrl != "https://127.0.0.1:5287/api/payments/vnpay/return/redirect"
            || config["PaymentReturn:FrontendResultUrl"] != "http://127.0.0.1:5289/payment/result"
            || (config.GetValue<bool>("Payment:Enabled") && !gateway.IsValid()))
            throw new InvalidOperationException("PaymentLocal requires sandbox credentials and fixed local return destinations.");
        return connection.ConnectionString;
    }

    public static NpgsqlDataSource CreateDataSource(IConfiguration config)
    {
        var builder = new NpgsqlDataSourceBuilder(ValidateConfiguration(config));
        const string sql = """
            SELECT current_database()='bonbon_payment_local' AND current_user='bonbon_payment_local_runner'
              AND session_user=current_user AND inet_server_addr()='127.0.0.1'::inet AND inet_server_port()=55432
              AND shobj_description(d.oid,'pg_database')='bonbon-payment-local:v1'
              AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolbypassrls
              AND current_setting('server_version_num')::int BETWEEN 160000 AND 169999
            FROM pg_database d JOIN pg_roles r ON r.rolname=current_user WHERE d.datname=current_database()
            """;
        builder.UsePhysicalConnectionInitializer(
            c => { using var command = new NpgsqlCommand(sql,c); if(command.ExecuteScalar() is not true) throw new InvalidOperationException("Local database identity rejected."); },
            async c => { await using var command = new NpgsqlCommand(sql,c); if(await command.ExecuteScalarAsync() is not true) throw new InvalidOperationException("Local database identity rejected."); });
        return builder.Build();
    }

    public static async Task VerifySchemaAsync(NpgsqlDataSource source, CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM bonbon.payment_gateway_schema WHERE version=1)
              AND (SELECT count(*)=5 FROM information_schema.columns WHERE table_schema='bonbon' AND table_name='booking'
                   AND column_name IN ('weekly_discount_percent_snapshot','discount_amount_snapshot','included_mileage_km_snapshot','overage_fee_per_km_snapshot','rental_terms_snapshot'))
              AND EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                   WHERE n.nspname='bonbon' AND p.proname='protect_booking_quote' AND p.prosrc LIKE '%discount_amount_snapshot%')
              AND (SELECT count(*)=4 FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                   WHERE n.nspname='bonbon' AND i.indisunique AND i.indisvalid AND i.indisready
                   AND c.relname IN ('uq_vnpay_sandbox_active','uq_vnpay_sandbox_idempotency','uq_vnpay_sandbox_accepted_success','uq_vnpay_sandbox_transaction'))
            """,connection);
        if(await command.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Local Payment schema is incomplete.");
    }
}
