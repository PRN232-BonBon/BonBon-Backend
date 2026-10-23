using System.Text.Json;
using Npgsql;
using BonBon.API.Configuration;

namespace BonBon.Tests.Integration;

// Separate from DisposablePostgresDatabase: no CREATE/DROP DATABASE or schema setup.
public static class SharedPostgresSettings
{
    public static bool HasConfiguration =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BONBON_SHARED_POSTGRES_CONNECTION")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BONBON_SHARED_POSTGRES_CONFIG_FILE"));

    public static string LoadReadOnlyConnection()
    {
        var connection = Environment.GetEnvironmentVariable("BONBON_SHARED_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            var path = Environment.GetEnvironmentVariable("BONBON_SHARED_POSTGRES_CONFIG_FILE");
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Shared test configuration is absent.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            connection = document.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString();
        }
        return ValidateReadOnlyConnection(connection!,LoadExpectedHost(Environment.GetEnvironmentVariable("BONBON_SHARED_POSTGRES_CONFIG_FILE")));
    }

    public static string LoadExpectedHost(string? configPath)
    {
        var host=Environment.GetEnvironmentVariable("BONBON_PAYMENT_EXPECTED_DB_HOST");
        if(string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(configPath))
        {
            using var document=JsonDocument.Parse(File.ReadAllText(configPath));
            if(document.RootElement.TryGetProperty("PaymentStaging",out var section)
                && section.TryGetProperty("ExpectedDatabaseHost",out var configured)) host=configured.GetString();
        }
        // Independent operator pin, never inferred from the supplied connection string.
        return PaymentStaging.RequireExpectedDatabaseHost(host);
    }

    public static string ValidateReadOnlyConnection(string connection,string expectedHost)
    {
        expectedHost=PaymentStaging.RequireExpectedDatabaseHost(expectedHost);
        var builder = new NpgsqlConnectionStringBuilder(connection);
        if (builder.Database != "bonbon" || builder.Username != "bonbon_app" ||
            builder.Host != expectedHost || builder.Port != 5432)
            throw new InvalidOperationException("Shared tests require the explicitly approved BonBon Tailscale target.");
        builder.Pooling = false;
        builder.Timeout = 5;
        builder.CommandTimeout = 5;
        builder.IncludeErrorDetail = false;
        builder.LogParameters = false;
        builder.ApplicationName = "bonbon_approval_readonly";
        builder.Options = "-c default_transaction_read_only=on -c statement_timeout=5000 -c lock_timeout=1000 -c idle_in_transaction_session_timeout=10000";
        return builder.ConnectionString;
    }
}

public sealed class SharedReadOnlyFactAttribute : FactAttribute
{
    public SharedReadOnlyFactAttribute()
    {
        if (!SharedPostgresSettings.HasConfiguration)
            Skip = "No explicit shared PostgreSQL secret/configuration supplied; no connection attempted.";
    }
}
