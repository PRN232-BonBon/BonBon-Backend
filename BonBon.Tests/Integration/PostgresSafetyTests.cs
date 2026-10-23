namespace BonBon.Tests.Integration;

public class PostgresSafetyTests
{
    [Theory]
    [InlineData("Host=shared.example;Database=bonbon;Username=bonbon_app")]
    [InlineData("Host=db.example.test;Database=postgres;Username=bonbon_app")]
    [InlineData("Host=db.example.test;Database=bonbon;Username=postgres")]
    public void Shared_read_only_probe_rejects_unapproved_targets(string connection) =>
        Assert.Throws<InvalidOperationException>(() => SharedPostgresSettings.ValidateReadOnlyConnection(connection,"db.example.test"));

    [Fact]
    public void Shared_probe_enforces_server_read_only_and_timeouts()
    {
        var result = new Npgsql.NpgsqlConnectionStringBuilder(SharedPostgresSettings.ValidateReadOnlyConnection(
            "Host=db.example.test;Database=bonbon;Username=bonbon_app;Include Error Detail=true;Log Parameters=true","db.example.test"));
        Assert.Contains("default_transaction_read_only=on", result.Options);
        Assert.Contains("lock_timeout=1000", result.Options);
        Assert.False(result.IncludeErrorDetail);
        Assert.False(result.LogParameters);
        Assert.False(result.Pooling);
    }

    [Fact]
    public void Shared_probe_requires_an_independent_host_pin_before_connecting() =>
        Assert.Throws<InvalidOperationException>(()=>SharedPostgresSettings.ValidateReadOnlyConnection(
            "Host=db.example.test;Database=bonbon;Username=bonbon_app",""));

    [Theory]
    [InlineData("Host=shared.example;Database=postgres;Username=test")]
    [InlineData("Host=127.0.0.1;Database=bonbon;Username=test")]
    [InlineData("Host=localhost,shared.example;Database=postgres;Username=test")]
    public void Shared_or_application_database_configuration_is_rejected_before_connecting(string connection) =>
        Assert.Throws<InvalidOperationException>(() => DisposablePostgresDatabase.ValidateAdmin(connection));
}
