namespace Strata.Api.IntegrationTests;

// All integration tests share one physical test database, so they must run
// sequentially, not just within this collection but across the whole
// assembly — see AssemblyInfo.cs for the assembly-wide parallelization switch.
[CollectionDefinition(Name)]
public class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>
{
    public const string Name = "Integration Tests";
}
