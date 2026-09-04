namespace Strata.Api.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected IntegrationTestFixture Fixture { get; }

    protected IntegrationTestBase(IntegrationTestFixture fixture)
    {
        Fixture = fixture;
    }

    // Runs before every test method (xUnit creates a new instance of the test
    // class per test) — resets the shared test database to a clean state.
    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;
}
