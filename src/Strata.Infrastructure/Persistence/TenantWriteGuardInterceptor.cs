using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Strata.Application.Tenancy;
using Strata.Domain;

namespace Strata.Infrastructure.Persistence;

// Write-side counterpart to AppDbContext's global query filters: those only
// rewrite SELECT, so nothing stops an Added or Deleted ITenantOwned entity
// from carrying the wrong TenantId. Validates only — it never assigns a
// TenantId, so create paths stay responsible for setting it themselves.
public class TenantWriteGuardInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentTenant _currentTenant;

    public TenantWriteGuardInterceptor(ICurrentTenant currentTenant)
    {
        _currentTenant = currentTenant;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Validate(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Validate(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Validate(DbContext? context)
    {
        if (context is null || !_currentTenant.IsAvailable)
        {
            // No request-scoped tenant context at all (migrations, test/admin
            // seeding via a root-provider scope) — nothing to validate
            // against. Distinct from a request whose claim is invalid, which
            // TenantId itself still throws on below.
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not ITenantOwned tenantOwned)
            {
                continue;
            }

            if (entry.State is not (EntityState.Added or EntityState.Deleted))
            {
                continue;
            }

            if (tenantOwned.TenantId != _currentTenant.TenantId)
            {
                throw new InvalidOperationException(
                    $"Refusing to {entry.State.ToString().ToLowerInvariant()} a " +
                    $"{entry.Entity.GetType().Name} whose TenantId ({tenantOwned.TenantId}) " +
                    $"does not match the current tenant ({_currentTenant.TenantId}).");
            }
        }
    }
}
