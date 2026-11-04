using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BonBon.Business.Services.Implementations;
using BonBon.DataAccess;
using BonBon.DataAccess.Repositories.Implementations;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

namespace BonBon.Tests.Integration;

public class SharedPostgresReadOnlyTests(ITestOutputHelper output)
{
    [SharedReadOnlyFact]
    public async Task Target_and_schema_are_inspected_without_writes()
    {
        await SafeDatabaseCall(async () =>
        {
            await using var connection = new NpgsqlConnection(SharedPostgresSettings.LoadReadOnlyConnection());
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var identity = new NpgsqlCommand("""
                SELECT current_database(), current_user, current_setting('server_version'),
                    current_setting('transaction_read_only'), pg_is_in_recovery()
                """, connection, transaction))
            {
                await using var reader = await identity.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("bonbon", reader.GetString(0));
                Assert.Equal("bonbon_app", reader.GetString(1));
                Assert.StartsWith("16.", reader.GetString(2));
                Assert.Equal("on", reader.GetString(3));
                output.WriteLine($"Target: database={reader.GetString(0)}, user={reader.GetString(1)}, PostgreSQL={reader.GetString(2)}, read_only={reader.GetString(3)}, recovery={reader.GetBoolean(4)}");
            }

            await using (var columns = new NpgsqlCommand("""
                SELECT table_name, column_name, data_type, is_generated, coalesce(generation_expression, '')
                FROM information_schema.columns WHERE table_schema = 'bonbon'
                  AND table_name IN ('account', 'account_role', 'vehicle', 'booking', 'availability_block')
                ORDER BY table_name, ordinal_position
                """, connection, transaction))
            {
                var bookingColumns = new HashSet<string>();
                await using var reader = await columns.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    output.WriteLine($"Column: {reader.GetString(0)}.{reader.GetString(1)} {reader.GetString(2)} generated={reader.GetString(3)} {reader.GetString(4)}");
                    if (reader.GetString(0) == "booking") bookingColumns.Add(reader.GetString(1));
                    if (reader.GetString(0) == "booking" && reader.GetString(1) == "quoted_rental_amount")
                        Assert.Equal("ALWAYS", reader.GetString(3));
                }
                foreach (var required in new[] { "booking_id", "customer_account_id", "vehicle_id", "billable_units",
                    "start_at", "end_at", "unit_price_snapshot", "quoted_rental_amount", "currency", "status" })
                    Assert.Contains(required, bookingColumns);
            }

            await using (var constraints = new NpgsqlCommand("""
                SELECT c.relname, co.conname, co.contype::text, pg_get_constraintdef(co.oid), co.convalidated
                FROM pg_constraint co JOIN pg_class c ON c.oid = co.conrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'bonbon' AND c.relname IN ('account', 'account_role', 'vehicle', 'booking', 'availability_block')
                ORDER BY c.relname, co.conname
                """, connection, transaction))
            {
                var hasExclusion = false;
                await using var reader = await constraints.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    output.WriteLine($"Constraint: {reader.GetString(0)}.{reader.GetString(1)} validated={reader.GetBoolean(4)} {reader.GetString(3)}");
                    if (reader.GetString(0) == "booking" && reader.GetString(2) == "x")
                        hasExclusion = reader.GetBoolean(4) && reader.GetString(3).Contains("vehicle_id") && reader.GetString(3).Contains("AwaitingPayment");
                }
                Assert.True(hasExclusion, "Validated booking schedule exclusion constraint is required.");
            }

            var baseline = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "001_initial_schema.sql"));
            var migration002 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "002_flexible_booking_duration.sql"));
            await using (var functions = new NpgsqlCommand("""
                SELECT p.proname, pg_get_function_identity_arguments(p.oid), p.prosrc
                FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'bonbon' AND p.proname IN ('create_booking', 'has_active_role',
                    'check_actor_eligibility', 'protect_booking_quote', 'protect_vehicle_owner', 'check_vehicle_schedule')
                ORDER BY p.proname
                """, connection, transaction))
            {
                var hasCreateBooking = false;
                await using var reader = await functions.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var body = reader.GetString(2);
                    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
                    var normalized = Regex.Replace(body, @"\s+", " ").Trim();
                    var matchesBaseline = Regex.Replace(baseline, @"\s+", " ").Contains(normalized, StringComparison.Ordinal);
                    var matches002 = Regex.Replace(migration002, @"\s+", " ").Contains(normalized, StringComparison.Ordinal);
                    output.WriteLine($"Function: {reader.GetString(0)}({reader.GetString(1)}) fingerprint=suppressed matches_001={matchesBaseline} matches_002={matches002}");
                    if (reader.GetString(0) == "protect_booking_quote")
                    {
                        output.WriteLine($"Quote guard inspection: has_customer_immutability_check={body.Contains("NEW.customer_account_id IS DISTINCT FROM OLD.customer_account_id")}, has_pending_return_guard={body.Contains("NEW.status = 'PendingApproval'")}");
                        if (!matchesBaseline)
                            output.WriteLine("SCHEMA_REVIEW_REQUIRED: protect_booking_quote differs from current 001; catalog inspection passing is not deployment approval.");
                    }
                    if (reader.GetString(0) == "create_booking")
                        hasCreateBooking |= body.Contains("p_end_at") && body.Contains("billable_units");
                }
                Assert.True(hasCreateBooking, "Flexible create_booking(start_at,end_at) function is required.");
            }

            await using (var triggers = new NpgsqlCommand("""
                SELECT c.relname, t.tgname, t.tgenabled::text, pg_get_triggerdef(t.oid), p.proname
                FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace JOIN pg_proc p ON p.oid = t.tgfoid
                WHERE n.nspname = 'bonbon' AND NOT t.tgisinternal
                  AND c.relname IN ('account', 'account_role', 'vehicle', 'booking', 'availability_block')
                ORDER BY c.relname, t.tgname
                """, connection, transaction))
            {
                var scheduleTables = new HashSet<string>();
                await using var reader = await triggers.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    output.WriteLine($"Trigger: {reader.GetString(0)}.{reader.GetString(1)} enabled={reader.GetString(2)} {reader.GetString(3)}");
                    if (reader.GetString(4) == "check_vehicle_schedule" && reader.GetString(2) is "O" or "A")
                        scheduleTables.Add(reader.GetString(0));
                }
                Assert.Contains("booking", scheduleTables);
                Assert.Contains("availability_block", scheduleTables);
            }
            await transaction.RollbackAsync();
            output.WriteLine("Read-only catalog inspection rolled back. No application rows created or changed.");
        });
    }

    [SharedReadOnlyFact]
    public async Task Payment_schema_metadata_is_inspected_without_writes()
    {
        await SafeDatabaseCall(async () =>
        {
            await using var connection = new NpgsqlConnection(SharedPostgresSettings.LoadReadOnlyConnection());
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var identity = new NpgsqlCommand("SELECT current_database(), current_user, current_setting('transaction_read_only'), current_setting('server_version')", connection, transaction))
            {
                await using var reader = await identity.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("bonbon", reader.GetString(0));
                Assert.Equal("bonbon_app", reader.GetString(1));
                Assert.Equal("on", reader.GetString(2));
                output.WriteLine($"Payment metadata target: database={reader.GetString(0)}, user={reader.GetString(1)}, read_only={reader.GetString(2)}, version={reader.GetString(3)}");
            }
            // Only catalogs: no application rows, DDL, migration, locking or business functions.
            var queries = new[]
            {
                """
                SELECT 'COLUMN ' || c.relname || '.' || a.attname || ' ' || format_type(a.atttypid,a.atttypmod)
                    || ' notnull=' || a.attnotnull::text || ' generated=' || a.attgenerated::text
                    || ' expression=' || coalesce(pg_get_expr(d.adbin,d.adrelid),'')
                FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
                WHERE n.nspname='bonbon' AND c.relname IN ('booking','payment','wallet','wallet_transaction')
                    AND a.attnum>0 AND NOT a.attisdropped ORDER BY c.relname,a.attnum
                """,
                """
                SELECT 'CONSTRAINT ' || c.relname || '.' || co.conname || ' validated=' || co.convalidated::text || ' ' || pg_get_constraintdef(co.oid)
                FROM pg_constraint co JOIN pg_class c ON c.oid=co.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='bonbon' AND c.relname IN ('booking','payment','wallet','wallet_transaction') ORDER BY c.relname,co.conname
                """,
                """
                SELECT 'INDEX ' || indexname || ' ' || indexdef FROM pg_indexes
                WHERE schemaname='bonbon' AND tablename IN ('booking','payment','wallet','wallet_transaction') ORDER BY tablename,indexname
                """,
                """
                SELECT 'TRIGGER enabled=' || t.tgenabled::text || ' ' || pg_get_triggerdef(t.oid)
                FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='bonbon' AND NOT t.tgisinternal AND c.relname IN ('booking','payment','wallet','wallet_transaction') ORDER BY c.relname,t.tgname
                """,
                """
                SELECT 'PRIVILEGE ' || c.relname || ' owner=' || pg_get_userbyid(c.relowner)
                    || ' select=' || has_table_privilege(c.oid,'SELECT')::text
                    || ' insert=' || has_table_privilege(c.oid,'INSERT')::text
                    || ' update=' || has_table_privilege(c.oid,'UPDATE')::text
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='bonbon' AND c.relkind='r' AND c.relname IN ('booking','payment','wallet','wallet_transaction') ORDER BY c.relname
                """,
                """
                SELECT 'PAYMENT_SCHEMA_PRESENT=' || (to_regclass('bonbon.payment_gateway_schema') IS NOT NULL)::text
                    || ' NOTIFICATION_PRESENT=' || (to_regclass('bonbon.payment_notification') IS NOT NULL)::text
                """,
            };
            foreach (var sql in queries)
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) output.WriteLine(reader.GetString(0));
            }
            await transaction.RollbackAsync();
            output.WriteLine("Catalog inspection only; no application data read or changed.");
        });
    }

    [SharedReadOnlyFact]
    public async Task Payment_test_database_permissions_are_inspected_without_writes()
    {
        await SafeDatabaseCall(async () =>
        {
            await using var connection = new NpgsqlConnection(SharedPostgresSettings.LoadReadOnlyConnection());
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT current_database(),current_user,current_setting('transaction_read_only'),
                    inet_server_addr()::text,inet_server_port(),current_setting('server_version'),
                    r.rolsuper,r.rolcreatedb,r.rolcreaterole,
                    (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname='bonbon_payment_test'),
                    (SELECT shobj_description(oid,'pg_database') FROM pg_database WHERE datname='bonbon_payment_test')
                FROM pg_roles r WHERE r.rolname=current_user
                """, connection, transaction);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("bonbon",reader.GetString(0)); Assert.Equal("bonbon_app",reader.GetString(1));
                Assert.Equal("on",reader.GetString(2));
                output.WriteLine("Read-only identity inspected; server address and administrative privileges suppressed.");
                output.WriteLine($"Test database: exists={!reader.IsDBNull(9)}, owner_present={!reader.IsDBNull(9)}, marker_present={!reader.IsDBNull(10)}; values suppressed.");
            }
            await transaction.RollbackAsync();
        });
    }

    [SharedReadOnlyFact]
    public async Task Missing_booking_returns_not_found_in_a_read_only_session()
    {
        await SafeDatabaseCall(async () =>
        {
            var connectionString = SharedPostgresSettings.LoadReadOnlyConnection();
            var bookingId = Guid.NewGuid();
            // No real ID is used. The read-only session also prevents row locking/writes if it exists.
            await using var context = new BonBonDbContext(new DbContextOptionsBuilder<BonBonDbContext>()
                .UseNpgsql(connectionString).Options);
            var service = new BookingService(new BookingRepository(context));
            var approved = await service.ApproveAsync(Guid.NewGuid(), bookingId);
            var rejected = await service.RejectAsync(Guid.NewGuid(), bookingId);
            Assert.Equal("BOOKING_NOT_FOUND", approved.ErrorCode);
            Assert.Equal("BOOKING_NOT_FOUND", rejected.ErrorCode);
            output.WriteLine("Approve/reject of a random absent booking returned BOOKING_NOT_FOUND; no writes.");
        });
    }

    private static async Task SafeDatabaseCall(Func<Task> action)
    {
        try { await action(); }
        catch (NpgsqlException exception)
        {
            // Do not propagate connection strings, server detail or credentials to test logs.
            throw new InvalidOperationException($"Shared PostgreSQL check failed: {exception.GetType().Name}; SQLSTATE={exception.SqlState ?? "unavailable"}.");
        }
    }
}
