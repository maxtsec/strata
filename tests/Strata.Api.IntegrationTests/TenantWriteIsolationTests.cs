using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Strata.Application.Tenancy;
using Strata.Domain.Documents;

namespace Strata.Api.IntegrationTests;

// Directly exercises TenantWriteGuardInterceptor's enforcement, not just that
// legitimate app writes still work. QueryDbAsync can't do this — its
// DbContext has no HttpContext, so IsAvailable is false and validation is
// skipped by design — so these use Fixture.CreateDbContext with a fixed,
// always-available fake ICurrentTenant standing in for "this request belongs
// to tenant A", independent of the real HTTP/DI pipeline.
public class TenantWriteIsolationTests : IntegrationTestBase
{
    public TenantWriteIsolationTests(IntegrationTestFixture fixture) : base(fixture)
    {
    }

    private sealed class FixedCurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantId { get; } = tenantId;
        public bool IsAvailable => true;
    }

    private async Task<(HttpClient Client, Guid UserId, Guid TenantId)> RegisterAsync(string email)
    {
        var client = Fixture.Factory.CreateClient();
        var token = await TestApiHelpers.RegisterAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, TestApiHelpers.UserIdFromToken(token), TestApiHelpers.TenantIdFromToken(token));
    }

    [Fact]
    public async Task Adding_a_folder_with_a_foreign_tenant_id_is_rejected_and_not_persisted()
    {
        var (_, userAId, tenantAId) = await RegisterAsync("write-add-foreign-a@test.local");
        var (_, _, tenantBId) = await RegisterAsync("write-add-foreign-b@test.local");
        var folderId = Guid.NewGuid();

        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(tenantAId));
        db.Folders.Add(new Folder { Id = folderId, OwnerId = userAId, TenantId = tenantBId, Name = "adversarial" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        var exists = await Fixture.QueryDbAsync(d =>
            d.Folders.AsNoTracking().IgnoreQueryFilters().AnyAsync(f => f.Id == folderId));
        Assert.False(exists);
    }

    [Fact]
    public async Task Adding_a_folder_with_the_current_tenant_id_succeeds()
    {
        var (_, userAId, tenantAId) = await RegisterAsync("write-add-legit-a@test.local");
        var folderId = Guid.NewGuid();

        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(tenantAId));
        db.Folders.Add(new Folder { Id = folderId, OwnerId = userAId, TenantId = tenantAId, Name = "legit" });
        await db.SaveChangesAsync();

        var exists = await Fixture.QueryDbAsync(d =>
            d.Folders.AsNoTracking().IgnoreQueryFilters().AnyAsync(f => f.Id == folderId));
        Assert.True(exists);
    }

    [Fact]
    public async Task Deleting_a_stub_entity_for_a_foreign_tenant_folder_is_rejected_and_leaves_it_in_place()
    {
        var (_, userAId, tenantAId) = await RegisterAsync("write-delete-foreign-a@test.local");
        var (clientB, _, tenantBId) = await RegisterAsync("write-delete-foreign-b@test.local");
        var tenantBFolderId = await TestApiHelpers.CreateFolderAsync(clientB, "B's folder");

        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(tenantAId));
        db.Folders.Remove(new Folder { Id = tenantBFolderId, OwnerId = userAId, TenantId = tenantBId, Name = "stub" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        var exists = await Fixture.QueryDbAsync(d =>
            d.Folders.AsNoTracking().IgnoreQueryFilters().AnyAsync(f => f.Id == tenantBFolderId));
        Assert.True(exists);
    }

    [Fact]
    public async Task Deleting_a_stub_entity_for_the_current_tenants_own_folder_succeeds()
    {
        var (clientA, userAId, tenantAId) = await RegisterAsync("write-delete-legit-a@test.local");
        var folderId = await TestApiHelpers.CreateFolderAsync(clientA, "A's folder");

        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(tenantAId));
        db.Folders.Remove(new Folder { Id = folderId, OwnerId = userAId, TenantId = tenantAId, Name = "stub" });
        await db.SaveChangesAsync();

        var exists = await Fixture.QueryDbAsync(d =>
            d.Folders.AsNoTracking().IgnoreQueryFilters().AnyAsync(f => f.Id == folderId));
        Assert.False(exists);
    }
}
