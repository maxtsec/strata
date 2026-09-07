using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Strata.Domain.Documents;

namespace Strata.Api.IntegrationTests;

// Directly exercises TenantWriteGuardInterceptor's enforcement, not just that
// legitimate app writes still work. QueryDbAsync can't do this — its
// DbContext has no HttpContext, so IsAvailable is false there, and any
// tenant-owned write through it now unconditionally fails closed regardless
// of which tenant it's meant to represent — so these use
// Fixture.CreateDbContext with a fixed, always-available fake ICurrentTenant
// standing in for "this request belongs to tenant A", independent of the
// real HTTP/DI pipeline.
public class TenantWriteIsolationTests : IntegrationTestBase
{
    public TenantWriteIsolationTests(IntegrationTestFixture fixture) : base(fixture)
    {
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

    [Fact]
    public async Task Deleting_a_foreign_folder_via_a_stub_forged_with_the_current_tenant_id_is_rejected_and_leaves_it_in_place()
    {
        // The dangerous case: the stub's own TenantId is forged to match the
        // acting tenant (A), not the row's real one (B) — proving the check
        // is against what the database actually has, not what the stub
        // merely claims about itself.
        var (_, userAId, tenantAId) = await RegisterAsync("write-delete-forged-a@test.local");
        var (clientB, _, _) = await RegisterAsync("write-delete-forged-b@test.local");
        var tenantBFolderId = await TestApiHelpers.CreateFolderAsync(clientB, "B's folder");

        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(tenantAId));
        db.Folders.Remove(new Folder { Id = tenantBFolderId, OwnerId = userAId, TenantId = tenantAId, Name = "stub" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        var exists = await Fixture.QueryDbAsync(d =>
            d.Folders.AsNoTracking().IgnoreQueryFilters().AnyAsync(f => f.Id == tenantBFolderId));
        Assert.True(exists);
    }

    [Fact]
    public async Task Retargeting_a_folder_to_a_foreign_tenant_via_update_is_rejected_and_leaves_it_unchanged()
    {
        var (clientA, userAId, tenantAId) = await RegisterAsync("write-modify-foreign-a@test.local");
        var (_, _, tenantBId) = await RegisterAsync("write-modify-foreign-b@test.local");
        var folderId = await TestApiHelpers.CreateFolderAsync(clientA, "A's folder");

        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(tenantAId));
        db.Update(new Folder { Id = folderId, OwnerId = userAId, TenantId = tenantBId, Name = "hijacked" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        var folder = await Fixture.QueryDbAsync(d =>
            d.Folders.AsNoTracking().IgnoreQueryFilters().SingleAsync(f => f.Id == folderId));
        Assert.Equal("A's folder", folder.Name);
        Assert.Equal(tenantAId, folder.TenantId);
    }

    [Fact]
    public async Task Updating_a_folder_via_a_detached_entity_with_the_current_tenant_id_succeeds()
    {
        var (clientA, userAId, tenantAId) = await RegisterAsync("write-modify-legit-a@test.local");
        var folderId = await TestApiHelpers.CreateFolderAsync(clientA, "Original");

        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(tenantAId));
        db.Update(new Folder { Id = folderId, OwnerId = userAId, TenantId = tenantAId, Name = "Renamed" });
        await db.SaveChangesAsync();

        var folder = await Fixture.QueryDbAsync(d =>
            d.Folders.AsNoTracking().IgnoreQueryFilters().SingleAsync(f => f.Id == folderId));
        Assert.Equal("Renamed", folder.Name);
    }

    [Fact]
    public async Task Retargeting_a_foreign_folder_via_a_stub_forged_with_the_current_tenant_id_is_rejected_and_leaves_it_unchanged()
    {
        // Same forged-value attack as the delete case, but for Modified: the
        // stub claims TenantId = A (matching the actor) while the row it
        // targets by id actually belongs to B.
        var (_, userAId, tenantAId) = await RegisterAsync("write-modify-forged-a@test.local");
        var (clientB, _, _) = await RegisterAsync("write-modify-forged-b@test.local");
        var tenantBFolderId = await TestApiHelpers.CreateFolderAsync(clientB, "B's folder");

        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(tenantAId));
        db.Update(new Folder { Id = tenantBFolderId, OwnerId = userAId, TenantId = tenantAId, Name = "stolen" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        var folder = await Fixture.QueryDbAsync(d =>
            d.Folders.AsNoTracking().IgnoreQueryFilters().SingleAsync(f => f.Id == tenantBFolderId));
        Assert.Equal("B's folder", folder.Name);
    }

    [Fact]
    public async Task Sync_SaveChanges_also_rejects_a_foreign_tenant_id()
    {
        // All the tests above go through SaveChangesAsync, matching how the
        // app actually calls it — this confirms the sync SavingChanges
        // override, required by the interceptor interface but otherwise
        // unused by the app, enforces the same rule.
        var (_, userAId, tenantAId) = await RegisterAsync("write-sync-a@test.local");
        var (_, _, tenantBId) = await RegisterAsync("write-sync-b@test.local");
        var folderId = Guid.NewGuid();

        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(tenantAId));
        db.Folders.Add(new Folder { Id = folderId, OwnerId = userAId, TenantId = tenantBId, Name = "adversarial" });

        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());

        var exists = await Fixture.QueryDbAsync(d =>
            d.Folders.AsNoTracking().IgnoreQueryFilters().AnyAsync(f => f.Id == folderId));
        Assert.False(exists);
    }

    [Fact]
    public async Task Writing_a_tenant_owned_entity_with_no_current_tenant_available_is_rejected()
    {
        var (_, userAId, tenantAId) = await RegisterAsync("write-no-context-a@test.local");
        var folderId = Guid.NewGuid();

        await using var db = Fixture.CreateDbContext(new NoCurrentTenant());
        db.Folders.Add(new Folder { Id = folderId, OwnerId = userAId, TenantId = tenantAId, Name = "orphaned" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        var exists = await Fixture.QueryDbAsync(d =>
            d.Folders.AsNoTracking().IgnoreQueryFilters().AnyAsync(f => f.Id == folderId));
        Assert.False(exists);
    }
}
