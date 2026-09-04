using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Respawn;
using Strata.Infrastructure.Persistence;

namespace Strata.Api.IntegrationTests;

// Shared once per test collection: one real SQL Server test database, migrated
// once. ResetDatabaseAsync (called before every test, see IntegrationTestBase)
// uses Respawn to reset table contents directly against the database rather
// than relying on a transaction — a transaction opened here wouldn't cover
// writes made by HTTP requests through WebApplicationFactory, since those use
// their own scoped DbContext on a separate connection.
public class IntegrationTestFixture : IAsyncLifetime
{
    private const string ConnectionString =
        "Server=localhost,1433;Database=StrataIntegrationTests;User Id=sa;Password=Strata_Dev_2026!;TrustServerCertificate=True;MultipleActiveResultSets=true";

    public StrataWebApplicationFactory Factory { get; private set; } = null!;

    private Respawner _respawner = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("STRATA_TEST_CONNECTION_STRING") ?? ConnectionString;
        Factory = new StrataWebApplicationFactory(connectionString);

        // Touching Services starts the host, which is enough to force it to
        // exist; migrate explicitly against the same connection string.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
            TablesToIgnore = ["__EFMigrationsHistory"]
        });
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new SqlConnection(
            Environment.GetEnvironmentVariable("STRATA_TEST_CONNECTION_STRING") ?? ConnectionString);
        await connection.OpenAsync();
        await _respawner.ResetAsync(connection);

        FileStorage.Reset();
    }

    public FakeFileStorage FileStorage => Factory.FileStorage;

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
    }
}
