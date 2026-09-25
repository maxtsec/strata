using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Strata.Domain.Documents;

namespace Strata.Api.IntegrationTests;

// Exercises reads across two real tenants. Database composite foreign keys
// prevent a resource from being labelled as one tenant while naming another
// tenant's owner, so foreign resources are seeded with their actual owners.
// The direct filter test below verifies the query filters independently of
// the API's per-user owner authorization.
public class TenantReadIsolationTests : IntegrationTestBase
{
    public TenantReadIsolationTests(IntegrationTestFixture fixture) : base(fixture)
    {
    }

    private async Task<(HttpClient Client, Guid UserId, Guid TenantId)> AuthenticatedOwnerAsync(string email)
    {
        var client = Fixture.Factory.CreateClient();
        var token = await TestApiHelpers.RegisterAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, TestApiHelpers.UserIdFromToken(token), TestApiHelpers.TenantIdFromToken(token));
    }

    // Seed through a context representing the foreign tenant so the write
    // guard accepts it and the database verifies all relationship keys.
    private async Task<int> SeedForeignTenantFolderAsync(Guid folderId, Guid ownerId, Guid foreignTenantId, string name)
    {
        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(foreignTenantId));
        db.Folders.Add(new Folder { Id = folderId, OwnerId = ownerId, TenantId = foreignTenantId, Name = name });
        return await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<int> SeedForeignTenantDocumentAsync(Guid documentId, Guid ownerId, Guid foreignTenantId, string name)
    {
        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(foreignTenantId));
        db.Documents.Add(new Document
        {
            Id = documentId,
            OwnerId = ownerId,
            TenantId = foreignTenantId,
            Name = name,
            ContentType = "text/plain",
            Size = 1L
        });
        return await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<int> SeedForeignTenantShareAsync(Guid shareId, Guid documentId, Guid userId, Guid foreignTenantId)
    {
        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(foreignTenantId));
        db.DocumentShares.Add(new DocumentShare
        {
            Id = shareId,
            DocumentId = documentId,
            UserId = userId,
            TenantId = foreignTenantId,
            UserRole = DocumentShare.Role.Viewer
        });
        return await db.SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tenant_filters_hide_foreign_folders_documents_and_shares()
    {
        var (_, _, tenantAId) = await AuthenticatedOwnerAsync("isolation-filter-a@test.local");
        var (clientB, _, tenantBId) = await AuthenticatedOwnerAsync("isolation-filter-b@test.local");
        await TestApiHelpers.AuthenticatedSameTenantClientAsync(
            Fixture.Factory, tenantBId, "isolation-filter-recipient@test.local");

        var folderId = await TestApiHelpers.CreateFolderAsync(clientB, "B's folder");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientB, "B's document.txt", folderId);
        var shareId = await TestApiHelpers.CreateShareAsync(
            clientB, documentId, "isolation-filter-recipient@test.local", DocumentShare.Role.Viewer);

        await using var db = Fixture.CreateDbContext(new FixedCurrentTenant(tenantAId));
        Assert.False(await db.Folders.AnyAsync(folder => folder.Id == folderId));
        Assert.False(await db.Documents.AnyAsync(document => document.Id == documentId));
        Assert.False(await db.DocumentShares.AnyAsync(share => share.Id == shareId));

        Assert.True(await db.Folders.IgnoreQueryFilters().AnyAsync(folder => folder.Id == folderId));
        Assert.True(await db.Documents.IgnoreQueryFilters().AnyAsync(document => document.Id == documentId));
        Assert.True(await db.DocumentShares.IgnoreQueryFilters().AnyAsync(share => share.Id == shareId));
    }

    [Fact]
    public async Task List_folders_excludes_foreign_tenant_row()
    {
        var (client, _, _) = await AuthenticatedOwnerAsync("isolation-list-a@test.local");
        var (_, foreignOwnerId, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-list-b@test.local");
        var adversarialFolderId = Guid.NewGuid();
        await SeedForeignTenantFolderAsync(adversarialFolderId, foreignOwnerId, foreignTenantId, "Adversarial");

        var response = await client.GetAsync("/api/folders");
        var folders = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returnedIds = folders.EnumerateArray().Select(f => f.GetProperty("id").GetGuid());
        Assert.DoesNotContain(adversarialFolderId, returnedIds);
    }

    [Fact]
    public async Task Update_foreign_tenant_folder_returns_404_and_leaves_it_unchanged()
    {
        var (client, _, _) = await AuthenticatedOwnerAsync("isolation-update-a@test.local");
        var (_, foreignOwnerId, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-update-b@test.local");
        var adversarialFolderId = Guid.NewGuid();
        await SeedForeignTenantFolderAsync(adversarialFolderId, foreignOwnerId, foreignTenantId, "Original");

        var response = await client.PutAsJsonAsync(
            $"/api/folders/{adversarialFolderId}", new { Name = "Hijacked", ParentFolderId = (Guid?)null });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var folder = await Fixture.QueryDbAsync(db =>
            db.Folders.AsNoTracking().IgnoreQueryFilters().SingleAsync(f => f.Id == adversarialFolderId));
        Assert.Equal("Original", folder.Name);
    }

    [Fact]
    public async Task Delete_foreign_tenant_folder_returns_404_and_leaves_it_in_place()
    {
        var (client, _, _) = await AuthenticatedOwnerAsync("isolation-delete-a@test.local");
        var (_, foreignOwnerId, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-delete-b@test.local");
        var adversarialFolderId = Guid.NewGuid();
        await SeedForeignTenantFolderAsync(adversarialFolderId, foreignOwnerId, foreignTenantId, "Untouchable");

        var response = await client.DeleteAsync($"/api/folders/{adversarialFolderId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stillExists = await Fixture.QueryDbAsync(db =>
            db.Folders.AsNoTracking().IgnoreQueryFilters().AnyAsync(f => f.Id == adversarialFolderId));
        Assert.True(stillExists);
    }

    [Fact]
    public async Task Create_document_in_foreign_tenant_folder_returns_400_and_creates_nothing()
    {
        var (client, _, _) = await AuthenticatedOwnerAsync("isolation-createdoc-a@test.local");
        var (_, foreignOwnerId, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-createdoc-b@test.local");
        var adversarialFolderId = Guid.NewGuid();
        await SeedForeignTenantFolderAsync(adversarialFolderId, foreignOwnerId, foreignTenantId, "Adversarial folder");

        var documentCountBefore = await Fixture.QueryDbAsync(db =>
            db.Documents.AsNoTracking().IgnoreQueryFilters().CountAsync());

        var response = await client.PostAsJsonAsync("/api/documents", new
        {
            Name = "doc.txt",
            FolderId = adversarialFolderId,
            ContentType = "text/plain",
            Size = 1L
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var documentCountAfter = await Fixture.QueryDbAsync(db =>
            db.Documents.AsNoTracking().IgnoreQueryFilters().CountAsync());
        Assert.Equal(documentCountBefore, documentCountAfter);
        Assert.Equal(0, Fixture.FileStorage.UploadUriCallCount);
    }

    [Fact]
    public async Task Download_foreign_tenant_document_returns_404_and_never_calls_file_storage()
    {
        var (client, _, _) = await AuthenticatedOwnerAsync("isolation-download-a@test.local");
        var (_, foreignOwnerId, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-download-b@test.local");
        var adversarialDocumentId = Guid.NewGuid();
        await SeedForeignTenantDocumentAsync(adversarialDocumentId, foreignOwnerId, foreignTenantId, "adversarial.txt");

        var response = await client.GetAsync($"/api/documents/{adversarialDocumentId}/download");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, Fixture.FileStorage.DownloadUriCallCount);
    }

    [Fact]
    public async Task CreateShare_on_adversarial_document_returns_404_and_creates_no_share()
    {
        var (client, _, _) = await AuthenticatedOwnerAsync("isolation-createshare-a@test.local");
        var (_, foreignOwnerId, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-createshare-b@test.local");
        var adversarialDocumentId = Guid.NewGuid();
        await SeedForeignTenantDocumentAsync(adversarialDocumentId, foreignOwnerId, foreignTenantId, "adversarial.txt");

        var response = await client.PostAsJsonAsync($"/api/documents/{adversarialDocumentId}/shares",
            new { Email = "isolation-createshare-a@test.local", Role = DocumentShare.Role.Viewer });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var shareCount = await Fixture.QueryDbAsync(db =>
            db.DocumentShares.AsNoTracking().IgnoreQueryFilters().CountAsync(s => s.DocumentId == adversarialDocumentId));
        Assert.Equal(0, shareCount);
    }

    [Fact]
    public async Task ListShares_on_adversarial_document_returns_404()
    {
        var (client, _, _) = await AuthenticatedOwnerAsync("isolation-listshare-a@test.local");
        var (_, foreignOwnerId, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-listshare-b@test.local");
        var adversarialDocumentId = Guid.NewGuid();
        await SeedForeignTenantDocumentAsync(adversarialDocumentId, foreignOwnerId, foreignTenantId, "adversarial.txt");

        var response = await client.GetAsync($"/api/documents/{adversarialDocumentId}/shares");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteShare_on_adversarial_document_returns_404_and_leaves_share_in_place()
    {
        var (client, _, _) = await AuthenticatedOwnerAsync("isolation-deleteshare-a@test.local");
        var (_, documentOwnerId, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-deleteshare-b@test.local");
        var recipientClient = await TestApiHelpers.AuthenticatedSameTenantClientAsync(
            Fixture.Factory, foreignTenantId, "isolation-deleteshare-recipient@test.local");
        var recipientId = TestApiHelpers.UserIdFromToken(recipientClient.DefaultRequestHeaders.Authorization!.Parameter!);
        var adversarialDocumentId = Guid.NewGuid();
        await SeedForeignTenantDocumentAsync(adversarialDocumentId, documentOwnerId, foreignTenantId, "adversarial.txt");
        var adversarialShareId = Guid.NewGuid();
        await SeedForeignTenantShareAsync(adversarialShareId, adversarialDocumentId, recipientId, foreignTenantId);

        var response = await client.DeleteAsync($"/api/documents/{adversarialDocumentId}/shares/{adversarialShareId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stillExists = await Fixture.QueryDbAsync(db =>
            db.DocumentShares.AsNoTracking().IgnoreQueryFilters().AnyAsync(s => s.Id == adversarialShareId));
        Assert.True(stillExists);
    }
}
