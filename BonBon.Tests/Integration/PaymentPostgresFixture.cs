using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BonBon.DataAccess;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BonBon.Tests.Integration;

public sealed class PaymentPostgresFactAttribute : FactAttribute
{
    public PaymentPostgresFactAttribute()
    {
        if (!PaymentPostgresFixture.Enabled) Skip="Requires explicit isolated payment database consent/configuration; never uses the disposable/drop fixture.";
    }
}

[CollectionDefinition("PaymentPostgres", DisableParallelization=true)]
public sealed class PaymentPostgresCollection : ICollectionFixture<PaymentPostgresFixture> { }

// No DROP DATABASE, no cleanup of unknown records, no fallback to application DB.
public sealed class PaymentPostgresFixture : IAsyncLifetime
{
    public const string Database="bonbon_payment_test", Role="bonbon_payment_test_runner";
    public const string Consent="I_CONFIRM_BONBON_PAYMENT_TEST_ONLY";
    public static bool Enabled => Environment.GetEnvironmentVariable("BONBON_PAYMENT_TEST_CONSENT")==Consent
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BONBON_PAYMENT_TEST_CONFIG_FILE"));
    private string _connection="", _identity="", _serverAddress="", _marker="", _sourceConnection="";
    public string BaselineHash { get; private set; }="";
    public bool MigrationRollbackVerified { get; private set; }
    public bool SourceQuoteGuardWas002 { get; private set; }
    public Guid RunId { get; }=Guid.NewGuid();
    public List<Guid> Bookings { get; }=[];
    private static string Sql(string name)=>File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sql",name));
    private static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        try { await ProvisionAsync(); }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or JsonException)
        { throw new InvalidOperationException($"Isolated Payment setup stopped safely: {e.GetType().Name}; SQLSTATE={(e as NpgsqlException)?.SqlState ?? "n/a"}. Inspect catalogs/local test state; no database is dropped."); }
    }

    private async Task ProvisionAsync()
    {
        var config=Path.GetFullPath(Environment.GetEnvironmentVariable("BONBON_PAYMENT_TEST_CONFIG_FILE")!);
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(config));
        var raw=document.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString()!;
        var expectedHost=SharedPostgresSettings.LoadExpectedHost(config);
        var sourceConnection=SharedPostgresSettings.ValidateReadOnlyConnection(raw,expectedHost);
        _sourceConnection=sourceConnection;
        string baseline, quote; List<string> functions; List<string> nullability=[];
        await using (var source=new NpgsqlConnection(sourceConnection))
        {
            await source.OpenAsync();
            await using var identity=new NpgsqlCommand("SELECT system_identifier::text,inet_server_addr()::text,current_database(),current_user,current_setting('transaction_read_only') FROM pg_control_system()",source);
            await using (var reader=await identity.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync()); _identity=reader.GetString(0); _serverAddress=reader.GetString(1);
                Assert.Equal("bonbon",reader.GetString(2)); Assert.Equal("bonbon_app",reader.GetString(3)); Assert.Equal("on",reader.GetString(4));
            }
            functions=[];
            await using (var cmd=new NpgsqlCommand("""
                SELECT proname,pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='bonbon' AND proname IN ('has_active_role','check_actor_eligibility','check_message_participant',
                  'protect_vehicle_owner','protect_booking_quote','check_vehicle_schedule','create_booking') ORDER BY proname
                """,source))
            {
                await using var reader=await cmd.ExecuteReaderAsync();
                while(await reader.ReadAsync()) functions.Add(reader.GetString(1));
            }
            Assert.Equal(7,functions.Count);
            quote=functions.Single(x=>x.Contains("FUNCTION bonbon.protect_booking_quote()"));
            SourceQuoteGuardWas002=quote.Contains("'PendingApproval', 'Rejected', 'Cancelled'") && !quote.Contains("NEW.customer_account_id IS DISTINCT");
            baseline=await CatalogAsync(source); BaselineHash=Hash(baseline);
            await using(var columns=new NpgsqlCommand("""
                SELECT format('ALTER TABLE %I.%I ALTER COLUMN %I %s NOT NULL;',n.nspname,c.relname,a.attname,CASE WHEN a.attnotnull THEN 'SET' ELSE 'DROP' END)
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid
                WHERE n.nspname='bonbon' AND c.relkind='r' AND a.attnum>0 AND NOT a.attisdropped ORDER BY c.relname,a.attnum
                """,source))
            { await using var reader=await columns.ExecuteReaderAsync(); while(await reader.ReadAsync()) nullability.Add(reader.GetString(0)); }
            // Metadata proves the new login has no data-write rights on the main DB.
            await using var grants=new NpgsqlCommand("""
                SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=@role) THEN true ELSE
                  NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='bonbon' AND c.relkind IN ('r','p')
                      AND has_table_privilege(@role,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE,TRIGGER,REFERENCES')) END
                """,source);
            grants.Parameters.AddWithValue("role",Role);
            Assert.True((bool)(await grants.ExecuteScalarAsync())!);
        }

        var adminBuilder=new NpgsqlConnectionStringBuilder(sourceConnection) { Database="postgres",ApplicationName="bonbon_payment_test_provision",
            Options="-c statement_timeout=15000 -c lock_timeout=2000 -c idle_in_transaction_session_timeout=15000" };
        var credentialPath=Path.Combine(Path.GetDirectoryName(config)!,".payment-test.local.json");
        var fresh=false;
        await using (var admin=new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync(); await VerifyAsync(admin,"postgres","bonbon_app");
            await using var exists=new NpgsqlCommand("SELECT pg_get_userbyid(datdba),shobj_description(oid,'pg_database') FROM pg_database WHERE datname=@db",admin);
            exists.Parameters.AddWithValue("db",Database);
            string? owner=null,marker=null;
            await using(var reader=await exists.ExecuteReaderAsync()) if(await reader.ReadAsync())
            { owner=reader.GetString(0); marker=reader.IsDBNull(1)?null:reader.GetString(1); }
            if(owner is not null)
            {
                // Existing unmarked DB/unknown credentials is always a stop, never an overwrite.
                if(owner!=Role || marker!="bonbon-payment-test:v1:"+_identity || !File.Exists(credentialPath))
                    throw new InvalidOperationException("Existing database requires ownership/data review.");
                using var credentials=JsonDocument.Parse(await File.ReadAllTextAsync(credentialPath));
                _connection=credentials.RootElement.GetProperty("ConnectionString").GetString()!;
                _marker=marker;
            }
            else
            {
                await using var roleExists=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@role)",admin);
                roleExists.Parameters.AddWithValue("role",Role);
                if((bool)(await roleExists.ExecuteScalarAsync())!) throw new InvalidOperationException("Unknown existing test role; do not alter it.");
                var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await using(var logging=new NpgsqlCommand("SELECT current_setting('log_statement')='none' AND current_setting('log_min_duration_statement')='-1' AND current_setting('log_min_duration_sample')='-1' AND coalesce(current_setting('pgaudit.log',true),'none') IN ('','none')",admin))
                    if(!(bool)(await logging.ExecuteScalarAsync())!) throw new InvalidOperationException("Credential DDL logging policy needs operator review.");
                // Generated hex only; no user input or credentials enter output/logs.
                await VerifyAsync(admin,"postgres","bonbon_app");
                await using(var createRole=new NpgsqlCommand($"CREATE ROLE {Role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT CONNECTION LIMIT 4 PASSWORD '{password}'",admin))
                    await createRole.ExecuteNonQueryAsync();
                var builder=new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database=Database,Username=Role,Password=password,
                    ApplicationName="bonbon_payment_isolated_tests",Options="-c statement_timeout=10000 -c lock_timeout=2000 -c idle_in_transaction_session_timeout=15000" };
                _connection=builder.ConnectionString;
                await File.WriteAllTextAsync(credentialPath,JsonSerializer.Serialize(new { ConnectionString=_connection }));
                await VerifyAsync(admin,"postgres","bonbon_app");
                await using(var createDb=new NpgsqlCommand($"CREATE DATABASE {Database} OWNER {Role} TEMPLATE template0",admin)) await createDb.ExecuteNonQueryAsync();
                _marker="bonbon-payment-test:v1:"+_identity;
                await VerifyAsync(admin,"postgres","bonbon_app");
                await using(var comment=new NpgsqlCommand($"COMMENT ON DATABASE {Database} IS '{_marker}'",admin)) await comment.ExecuteNonQueryAsync();
                fresh=true;
            }
        }
        await using(var mainReadOnly=new NpgsqlConnection(sourceConnection))
        {
            await mainReadOnly.OpenAsync();
            await using var permissions=new NpgsqlCommand("""
                SELECT NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                  WHERE n.nspname='bonbon' AND c.relkind IN ('r','p')
                    AND has_table_privilege(@role,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE,TRIGGER,REFERENCES'))
                """,mainReadOnly);
            permissions.Parameters.AddWithValue("role",Role); Assert.True((bool)(await permissions.ExecuteScalarAsync())!);
        }
        // Check fixed connection parameters before connecting, and identity again on server.
        var validated=new NpgsqlConnectionStringBuilder(_connection);
        if(validated.Host!=expectedHost || validated.Port!=5432 || validated.Database!=Database || validated.Username!=Role)
            throw new InvalidOperationException("Unexpected isolated connection.");
        validated.Pooling=false; validated.IncludeErrorDetail=false; validated.LogParameters=false; _connection=validated.ConnectionString;
        await using(var test=await OpenAsync())
        {
            if(!fresh && await ScalarOnAsync<bool>(test,"SELECT to_regnamespace('bonbon') IS NULL AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname<>'information_schema' AND c.relkind IN ('r','p','v','m','f'))"))
                fresh=true; // Verified empty, marked, owned DB left by interrupted provisioning; no overwrite.
            if(!fresh && await ScalarOnAsync<bool>(test,"SELECT to_regclass('public.payment_test_control') IS NULL AND to_regclass('bonbon.payment_gateway_schema') IS NULL"))
            {
                // Resume only our marked, partially prepared schema with no application
                // data at all. Never overwrite records or rebuild/drop existing objects.
                List<string> tables=[];
                await using(var cmd=new NpgsqlCommand("SELECT quote_ident(n.nspname)||'.'||quote_ident(c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind='r' AND n.nspname NOT LIKE 'pg_%' AND n.nspname<>'information_schema' AND NOT(n.nspname='bonbon' AND c.relname='role')",test))
                { await using var reader=await cmd.ExecuteReaderAsync(); while(await reader.ReadAsync()) tables.Add(reader.GetString(0)); }
                foreach(var table in tables) Assert.Equal(0L,await ScalarOnAsync<long>(test,"SELECT count(*) FROM "+table));
                fresh=true;
            }
            if(!fresh)
            {
                // Retained data is reusable only when this fixture owns all seed IDs.
                Assert.Equal("ready",await ScalarOnAsync<string>(test,"SELECT stage FROM public.payment_test_control WHERE singleton=1"));
                Assert.True(BaselineHash==await ScalarOnAsync<string>(test,"SELECT source_schema_hash FROM public.payment_test_control WHERE singleton=1"),"Baseline fingerprint mismatch; values suppressed.");
                MigrationRollbackVerified=await ScalarOnAsync<bool>(test,"SELECT migration_rollback_verified FROM public.payment_test_control WHERE singleton=1");
                Assert.False(await ScalarOnAsync<bool>(test,"""
                    SELECT EXISTS(SELECT 1 FROM bonbon.account a WHERE NOT EXISTS
                      (SELECT 1 FROM public.payment_test_manifest m WHERE m.kind='account' AND m.record_id=a.account_id))
                    OR EXISTS(SELECT 1 FROM bonbon.booking b WHERE NOT EXISTS
                      (SELECT 1 FROM public.payment_test_manifest m WHERE m.kind='booking' AND m.record_id=b.booking_id))
                    """));
            }
            else
            {
                if(await ScalarOnAsync<bool>(test,"SELECT to_regnamespace('bonbon') IS NULL")) await ExecuteOnAsync(test,Sql("001_initial_schema.sql"));
                await ExecuteOnAsync(test,string.Join("\n",nullability)); // Catalog-derived identifiers, test DB only.
                foreach(var function in functions) await ExecuteOnAsync(test,function);
                Assert.Equal(baseline,await CatalogAsync(test));
                // A single division-by-zero on the test DB verifies DDL rollback. No
                // server crash, disk/network fault, long wait or load is injected.
                await Assert.ThrowsAsync<PostgresException>(()=>ExecuteOnAsync(test,Sql("004_vnpay_sandbox.sql").Replace("COMMIT;","SELECT 1/0; COMMIT;",StringComparison.Ordinal)));
                await ExecuteOnAsync(test,"ROLLBACK");
                Assert.Equal(baseline,await CatalogAsync(test));
                Assert.False(await ScalarOnAsync<bool>(test,"SELECT to_regclass('bonbon.payment_gateway_schema') IS NOT NULL"));
                MigrationRollbackVerified=true;
                await ExecuteOnAsync(test,Sql("004_vnpay_sandbox.sql"));
                await ExecuteOnAsync(test,Sql("005_booking_quote_guard.sql"));
                await ExecuteOnAsync(test,"""
                    CREATE TABLE public.payment_test_control(singleton integer PRIMARY KEY CHECK(singleton=1),stage text NOT NULL,source_schema_hash text NOT NULL,migration_rollback_verified boolean NOT NULL);
                    CREATE TABLE public.payment_test_manifest(run_id uuid NOT NULL,kind text NOT NULL,record_id uuid NOT NULL,PRIMARY KEY(kind,record_id));
                    """
                    + "INSERT INTO public.payment_test_control VALUES(1,'ready','"+BaselineHash+"',true);");
            }
        }
    }

    private async Task VerifyAsync(NpgsqlConnection connection,string database,string user)
    {
        await using var command=new NpgsqlCommand("SELECT current_database(),current_user,inet_server_addr()::text,inet_server_port(),current_setting('server_version'),(SELECT system_identifier::text FROM pg_control_system())",connection);
        // Non-superuser cannot call pg_control_system; identity there uses the fixed
        // address/version + database marker, checked below by its owner.
        if(user==Role) command.CommandText="SELECT current_database(),current_user,inet_server_addr()::text,inet_server_port(),current_setting('server_version'),shobj_description(oid,'pg_database'),(SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole FROM pg_roles WHERE rolname=current_user),pg_get_userbyid(datdba) FROM pg_database WHERE datname=current_database()";
        await using var reader=await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync()); Assert.Equal(database,reader.GetString(0)); Assert.Equal(user,reader.GetString(1));
        Assert.True(_serverAddress==reader.GetString(2),"Server address identity mismatch; values suppressed.");
        Assert.Equal(5432,reader.GetInt32(3)); Assert.Equal("16.15",reader.GetString(4));
        Assert.True((user==Role?_marker:_identity)==reader.GetString(5),"Server/database marker identity mismatch; values suppressed.");
        if(user==Role) { Assert.True(reader.GetBoolean(6)); Assert.Equal(Role,reader.GetString(7)); }
    }

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection=new NpgsqlConnection(_connection);
        try { await connection.OpenAsync(); await VerifyAsync(connection,Database,Role); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task<BonBonDbContext> ContextAsync()
    {
        // Verify every repository connection before passing it to code that can write.
        var connection=await OpenAsync();
        return new BonBonDbContext(new DbContextOptionsBuilder<BonBonDbContext>().UseNpgsql(connection,contextOwnsConnection:true).Options);
    }
    public async Task ExecuteAsync(string sql,params (string,object)[] parameters)
    { await using var c=await OpenAsync(); await ExecuteOnAsync(c,sql,parameters); }
    public async Task<T> ScalarAsync<T>(string sql,params (string,object)[] parameters)
    { await using var c=await OpenAsync(); return await ScalarOnAsync<T>(c,sql,parameters); }
    private static NpgsqlCommand Command(NpgsqlConnection c,string sql,(string,object)[] args)
    { var command=new NpgsqlCommand(sql,c); foreach(var (name,value) in args) command.Parameters.AddWithValue(name,value); return command; }
    private static async Task ExecuteOnAsync(NpgsqlConnection c,string sql,params (string,object)[] args)
    { await using var command=Command(c,sql,args); await command.ExecuteNonQueryAsync(); }
    private static async Task<T> ScalarOnAsync<T>(NpgsqlConnection c,string sql,params (string,object)[] args)
    { await using var command=Command(c,sql,args); return (T)(await command.ExecuteScalarAsync())!; }

    private static async Task<string> CatalogAsync(NpgsqlConnection connection)
    {
        // Schema only: all user tables, columns, constraints, indexes, triggers and
        // the seven functions copied above. Owner/ACL intentionally differ in test.
        await using var command=new NpgsqlCommand("""
            SELECT string_agg(value,E'\n' ORDER BY value) FROM (
              SELECT 'C:'||c.relname||':'||a.attname||':'||format_type(a.atttypid,a.atttypmod)||':'||a.attnotnull::text||':'||a.attgenerated::text||':'||coalesce(pg_get_expr(d.adbin,d.adrelid),'') value
              FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid
              LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum
              WHERE n.nspname='bonbon' AND c.relkind='r' AND a.attnum>0 AND NOT a.attisdropped
              UNION ALL SELECT 'K:'||c.relname||':'||co.conname||':'||pg_get_constraintdef(co.oid) FROM pg_constraint co JOIN pg_class c ON c.oid=co.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='bonbon'
              UNION ALL SELECT 'I:'||tablename||':'||indexdef FROM pg_indexes WHERE schemaname='bonbon'
              UNION ALL SELECT 'T:'||pg_get_triggerdef(t.oid)||':'||t.tgenabled::text FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='bonbon' AND NOT t.tgisinternal
              UNION ALL SELECT 'F:'||pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='bonbon' AND p.prokind='f'
            ) metadata
            """,connection);
        return Regex.Replace((string)(await command.ExecuteScalarAsync())!,@"\s+"," ").Trim();
    }

    public sealed record Seed(Guid Owner,Guid Customer,Guid Outsider,Guid Vehicle,Guid First,Guid Second,DateTimeOffset Start);
    public async Task<Seed> SeedAsync()
    {
        var seed=new Seed(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
            new DateTimeOffset(DateTime.UtcNow.Date.AddDays(10).AddHours(2),TimeSpan.Zero));
        await ExecuteAsync("""
            BEGIN;
            INSERT INTO bonbon.account(account_id,email,password_hash,full_name) VALUES
              (@owner,@owner::text||'@test.invalid','not-a-real-password-hash','Synthetic owner'),
              (@customer,@customer::text||'@test.invalid','not-a-real-password-hash','Synthetic customer'),
              (@other,@other::text||'@test.invalid','not-a-real-password-hash','Synthetic outsider');
            INSERT INTO bonbon.account_role(account_id,role_id,status) VALUES(@owner,3,'Active'),(@customer,2,'Active'),(@other,2,'Active');
            INSERT INTO bonbon.vehicle(vehicle_id,owner_account_id,plate_number,brand,model,seats,transmission,pickup_address,hour_price,day_price,status)
              VALUES(@vehicle,@owner,@plate,'Synthetic','Test',4,'Automatic','Test only',100000,650000,'Available');
            INSERT INTO bonbon.booking(booking_id,customer_account_id,vehicle_id,rental_type,billable_units,start_at,end_at,unit_price_snapshot)
              VALUES(@first,@customer,@vehicle,'Daily',2,@start,@end,650000),(@second,@customer,@vehicle,'Daily',2,@start,@end,650000);
            INSERT INTO public.payment_test_manifest VALUES(@run,'account',@owner),(@run,'account',@customer),(@run,'account',@other),
              (@run,'vehicle',@vehicle),(@run,'booking',@first),(@run,'booking',@second);
            COMMIT;
            """,("owner",seed.Owner),("customer",seed.Customer),("other",seed.Outsider),("vehicle",seed.Vehicle),
            ("plate","TEST-"+seed.Vehicle.ToString("N")[..20]),("first",seed.First),("second",seed.Second),
            ("start",seed.Start),("end",seed.Start.AddDays(2)),("run",RunId));
        Bookings.AddRange([seed.First,seed.Second]); return seed;
    }

    public async Task DisposeAsync()
    {
        if(!Enabled || string.IsNullOrEmpty(_sourceConnection)) return;
        await using var source=new NpgsqlConnection(_sourceConnection); await source.OpenAsync();
        Assert.True(BaselineHash==Hash(await CatalogAsync(source)),"Source catalog changed; fingerprints suppressed."); // Metadata only on main.
    }
}
