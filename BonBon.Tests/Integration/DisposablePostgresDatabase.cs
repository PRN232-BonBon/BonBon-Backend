using BonBon.DataAccess;
using BonBon.DataAccess.Repositories.Implementations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BonBon.Tests.Integration;

public sealed class LocalPostgresFactAttribute : FactAttribute
{
    public LocalPostgresFactAttribute()
    {
        if (!DisposablePostgresDatabase.IsExplicitlyEnabled)
            Skip = "Requires explicit opt-in to a disposable local PostgreSQL 16 server; shared DB is never used.";
    }
}

public sealed class LocalPostgresTheoryAttribute : TheoryAttribute
{
    public LocalPostgresTheoryAttribute()
    {
        if (!DisposablePostgresDatabase.IsExplicitlyEnabled)
            Skip = "Requires explicit opt-in to a disposable local PostgreSQL 16 server; shared DB is never used.";
    }
}

// No appsettings, .env, DefaultConnection or fallback connection is ever loaded here.
public sealed class DisposablePostgresDatabase : IAsyncDisposable
{
    public const string Consent = "I_CONFIRM_DISPOSABLE_LOCAL_POSTGRES";
    public static bool IsExplicitlyEnabled =>
        Environment.GetEnvironmentVariable("BONBON_TEST_POSTGRES_CONSENT") == Consent &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BONBON_TEST_POSTGRES_ADMIN"));

    private readonly string _adminConnection;
    private readonly string _databaseName = "bonbon_booking_test_" + Guid.NewGuid().ToString("N");
    private bool _created;
    public string ConnectionString { get; private set; } = "";
    public Guid Owner { get; } = Guid.NewGuid();
    public Guid Customer { get; } = Guid.NewGuid();
    public Guid Outsider { get; } = Guid.NewGuid();
    public Guid Vehicle { get; } = Guid.NewGuid();
    public Guid FirstBooking { get; } = Guid.NewGuid();
    public Guid SecondBooking { get; } = Guid.NewGuid();
    public DateTimeOffset Start { get; } = new(DateTime.UtcNow.Date.AddDays(10).AddHours(2), TimeSpan.Zero);

    private DisposablePostgresDatabase(string admin) => _adminConnection = ValidateAdmin(admin);

    public static string ValidateAdmin(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.Host is not ("localhost" or "127.0.0.1" or "::1") || builder.Database != "postgres")
            throw new InvalidOperationException("Tests require a dedicated loopback PostgreSQL server and admin database postgres.");
        builder.Pooling = false;
        builder.Timeout = 5;
        builder.CommandTimeout = 15;
        return builder.ConnectionString;
    }

    public static async Task<DisposablePostgresDatabase> CreateAsync()
    {
        if (!IsExplicitlyEnabled) throw new InvalidOperationException("Disposable PostgreSQL tests have not been enabled.");
        var database = new DisposablePostgresDatabase(Environment.GetEnvironmentVariable("BONBON_TEST_POSTGRES_ADMIN")!);
        try
        {
            await using var admin = new NpgsqlConnection(database._adminConnection);
            await admin.OpenAsync();
            if (admin.PostgreSqlVersion.Major != 16)
                throw new InvalidOperationException("Integration tests require PostgreSQL 16.");
            // Identifier is generated internally from a fixed prefix + Guid, never from configuration.
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database._databaseName}\"", admin))
                await create.ExecuteNonQueryAsync();
            database._created = true;
            var builder = new NpgsqlConnectionStringBuilder(database._adminConnection)
            {
                Database = database._databaseName, ApplicationName = "bonbon_approval_tests",
            };
            database.ConnectionString = builder.ConnectionString;
            await database.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "001_initial_schema.sql")));
            await database.SeedAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public BonBonDbContext CreateContext() => new(new DbContextOptionsBuilder<BonBonDbContext>()
        .UseNpgsql(ConnectionString).Options);

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task SeedAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO bonbon.account (account_id, email, password_hash, full_name) VALUES
                (@owner, 'owner@test.invalid', 'test-hash', 'Test owner'),
                (@customer, 'customer@test.invalid', 'test-hash', 'Test customer'),
                (@outsider, 'outsider@test.invalid', 'test-hash', 'Other owner');
            INSERT INTO bonbon.account_role (account_id, role_id, status) VALUES
                (@owner, 3, 'Active'), (@customer, 2, 'Active'), (@outsider, 3, 'Active');
            INSERT INTO bonbon.vehicle (vehicle_id, owner_account_id, plate_number, brand, model,
                seats, transmission, pickup_address, hour_price, day_price, status)
            VALUES (@vehicle, @owner, 'TEST-001', 'Test', 'Car', 4, 'Automatic', 'Test address', 100000, 650000, 'Available');
            INSERT INTO bonbon.booking (booking_id, customer_account_id, vehicle_id, rental_type,
                billable_units, start_at, end_at, unit_price_snapshot)
            VALUES (@first, @customer, @vehicle, 'Daily', 2, @start, @end, 650000),
                   (@second, @customer, @vehicle, 'Daily', 2, @start, @end, 650000);
            """, connection);
        command.Parameters.AddWithValue("owner", Owner);
        command.Parameters.AddWithValue("customer", Customer);
        command.Parameters.AddWithValue("outsider", Outsider);
        command.Parameters.AddWithValue("vehicle", Vehicle);
        command.Parameters.AddWithValue("first", FirstBooking);
        command.Parameters.AddWithValue("second", SecondBooking);
        command.Parameters.AddWithValue("start", Start);
        command.Parameters.AddWithValue("end", Start.AddDays(2));
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_created) return;
        await using var admin = new NpgsqlConnection(_adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        _created = false;
    }
}
