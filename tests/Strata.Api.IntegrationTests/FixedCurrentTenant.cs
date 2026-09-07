using Strata.Application.Tenancy;

namespace Strata.Api.IntegrationTests;

// Always-available ICurrentTenant standing in for "this write belongs to a
// specific tenant", independent of the real HTTP/DI pipeline — used with
// IntegrationTestFixture.CreateDbContext wherever a test needs to seed or
// mutate data as a specific tenant outside an actual authenticated request.
public sealed class FixedCurrentTenant(Guid tenantId) : ICurrentTenant
{
    public Guid TenantId { get; } = tenantId;
    public bool IsAvailable => true;
}
