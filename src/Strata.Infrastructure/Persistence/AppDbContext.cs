using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Strata.Domain.Documents;
using Strata.Domain.Tenancy;
using Strata.Infrastructure.Identity;
using Strata.Application.Persistence;
using Strata.Application.Tenancy;

namespace Strata.Infrastructure.Persistence;

public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>, IApplicationDbContext
{
    private readonly ICurrentTenant _currentTenant;

    // Only the ICurrentTenant reference is stored here — its TenantId is never
    // read during construction, so this stays safe to build outside an HTTP
    // request (migrations, test setup). See HttpContextCurrentTenant for the
    // lazy-resolution side of that contract.
    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentTenant currentTenant) : base(options)
    {
        _currentTenant = currentTenant;
    }

    public DbSet<Document> Documents { get; set; }
    public DbSet<DocumentShare> DocumentShares { get; set; }
    public DbSet<Folder> Folders { get; set; }
    public DbSet<Tenant> Tenants { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Read-side tenant isolation. `Tenant` and `ApplicationUser` are
        // deliberately not filtered here — authentication has to be able to
        // look a user up before a tenant is even established, and same-tenant
        // recipient validation is separate, later work. Each filter closes
        // over `_currentTenant` (not a plain field), so EF Core re-evaluates
        // it against the live DbContext instance on every query, not once at
        // model-build time.
        builder.Entity<Folder>().HasQueryFilter(f => f.TenantId == _currentTenant.TenantId);
        builder.Entity<Document>().HasQueryFilter(d => d.TenantId == _currentTenant.TenantId);
        builder.Entity<DocumentShare>().HasQueryFilter(s => s.TenantId == _currentTenant.TenantId);
    }
}