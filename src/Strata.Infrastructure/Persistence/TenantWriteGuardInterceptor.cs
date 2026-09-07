using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Strata.Application.Tenancy;
using Strata.Domain;

namespace Strata.Infrastructure.Persistence;

// Write-side counterpart to AppDbContext's global query filters: those only
// rewrite SELECT, so nothing stops an Added, Modified, or Deleted
// ITenantOwned entity from carrying the wrong TenantId. Validates only — it
// never assigns a TenantId, so create paths stay responsible for setting it
// themselves.
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

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(eventData.Context, cancellationToken);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Validate(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in RelevantEntries(context))
        {
            CheckAvailable(entry);

            // Deleted/Modified: the row must actually belong to the current
            // tenant right now. Never trust a stub's own claimed TenantId —
            // an entity attached without ever being queried can say
            // anything, including a forged value chosen to match.
            if (entry.State is EntityState.Deleted or EntityState.Modified)
            {
                CheckMatches(entry, ActualTenantIdInDatabase(entry.GetDatabaseValues()));
            }

            // Added/Modified: the value actually being written must also
            // match — this is what stops a legitimately-owned row being
            // retargeted to a different tenant via Update(...).
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                CheckMatches(entry, ((ITenantOwned)entry.Entity).TenantId);
            }
        }
    }

    private async Task ValidateAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in RelevantEntries(context))
        {
            CheckAvailable(entry);

            if (entry.State is EntityState.Deleted or EntityState.Modified)
            {
                CheckMatches(entry, ActualTenantIdInDatabase(await entry.GetDatabaseValuesAsync(cancellationToken)));
            }

            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                CheckMatches(entry, ((ITenantOwned)entry.Entity).TenantId);
            }
        }
    }

    private static IEnumerable<EntityEntry> RelevantEntries(DbContext context) =>
        context.ChangeTracker.Entries().Where(e =>
            e.Entity is ITenantOwned &&
            e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    private void CheckAvailable(EntityEntry entry)
    {
        // A tenant-owned write with no trusted tenant to check against is
        // itself refused — never treated as an implicitly trusted admin
        // bypass. Migrations never reach here (they don't go through
        // SaveChanges); anything that does needs an explicit tenant.
        if (!_currentTenant.IsAvailable)
        {
            throw new InvalidOperationException(
                $"Refusing to {entry.State.ToString().ToLowerInvariant()} a " +
                $"{entry.Entity.GetType().Name} outside a request with a trusted tenant.");
        }
    }

    // Null means the row is invisible under this tenant's own query filter
    // (or genuinely gone) — refused either way, without confirming which,
    // for the same anti-enumeration reason the controllers already collapse
    // "missing" and "foreign" into one response.
    private static Guid ActualTenantIdInDatabase(PropertyValues? databaseValues)
    {
        if (databaseValues is null)
        {
            throw new InvalidOperationException(
                "Refusing to modify or delete a row that does not exist for the current tenant.");
        }

        return (Guid)databaseValues[nameof(ITenantOwned.TenantId)]!;
    }

    private void CheckMatches(EntityEntry entry, Guid tenantId)
    {
        if (tenantId != _currentTenant.TenantId)
        {
            throw new InvalidOperationException(
                $"Refusing to {entry.State.ToString().ToLowerInvariant()} a " +
                $"{entry.Entity.GetType().Name} whose TenantId ({tenantId}) " +
                $"does not match the current tenant ({_currentTenant.TenantId}).");
        }
    }
}
