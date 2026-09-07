using Strata.Application.Tenancy;

namespace Strata.Api.IntegrationTests;

// Stands in for "no request-scoped tenant context at all" — IsAvailable is
// always false, and TenantId itself throws loudly if ever reached, since the
// interceptor must never get that far when IsAvailable is false.
public sealed class NoCurrentTenant : ICurrentTenant
{
    public Guid TenantId => throw new InvalidOperationException("TenantId accessed despite IsAvailable being false.");
    public bool IsAvailable => false;
}
