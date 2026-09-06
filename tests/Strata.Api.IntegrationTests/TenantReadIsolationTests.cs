using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Strata.Domain.Documents;

namespace Strata.Api.IntegrationTests;

// Proves the EF Core global query filters on AppDbContext (Folder, Document,
// DocumentShare) are what blocks cross-tenant reads — not owner
// authorization, which has always run on a per-user basis and knows nothing
// about tenants. Every adversarial row here has OwnerId set to the acting
// user's own id (so owner authorization alone would let it through) but
// TenantId set to a different, real tenant. That combination should never
// occur through the API as it stands today, but the tenant filter is
// expected to hold as a second, independent boundary if it ever did — the
// same defence-in-depth reasoning as the query filter plus the (still
// pending) SaveChanges interceptor.
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

    private Task<int> SeedAdversarialFolderAsync(Guid folderId, Guid ownerId, Guid foreignTenantId, string name) =>
        Fixture.QueryDbAsync(db =>
        {
            db.Folders.Add(new Folder { Id = folderId, OwnerId = ownerId, TenantId = foreignTenantId, Name = name });
            return db.SaveChangesAsync(CancellationToken.None);
        });

    private Task<int> SeedAdversarialDocumentAsync(Guid documentId, Guid ownerId, Guid foreignTenantId, string name) =>
        Fixture.QueryDbAsync(db =>
        {
            db.Documents.Add(new Document
            {
                Id = documentId,
                OwnerId = ownerId,
                TenantId = foreignTenantId,
                Name = name,
                ContentType = "text/plain",
                Size = 1L
            });
            return db.SaveChangesAsync(CancellationToken.None);
        });

    private Task<int> SeedAdversarialShareAsync(Guid shareId, Guid documentId, Guid userId, Guid foreignTenantId) =>
        Fixture.QueryDbAsync(db =>
        {
            db.DocumentShares.Add(new DocumentShare
            {
                Id = shareId,
                DocumentId = documentId,
                UserId = userId,
                TenantId = foreignTenantId,
                UserRole = DocumentShare.Role.Viewer
            });
            return db.SaveChangesAsync(CancellationToken.None);
        });

    [Fact]
    public async Task List_folders_excludes_adversarial_row_owned_by_current_user()
    {
        var (client, userId, _) = await AuthenticatedOwnerAsync("isolation-list-a@test.local");
        var (_, _, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-list-b@test.local");
        var adversarialFolderId = Guid.NewGuid();
        await SeedAdversarialFolderAsync(adversarialFolderId, userId, foreignTenantId, "Adversarial");

        var response = await client.GetAsync("/api/folders");
        var folders = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returnedIds = folders.EnumerateArray().Select(f => f.GetProperty("id").GetGuid());
        Assert.DoesNotContain(adversarialFolderId, returnedIds);
    }

    [Fact]
    public async Task Update_adversarial_folder_returns_404_and_leaves_it_unchanged()
    {
        var (client, userId, _) = await AuthenticatedOwnerAsync("isolation-update-a@test.local");
        var (_, _, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-update-b@test.local");
        var adversarialFolderId = Guid.NewGuid();
        await SeedAdversarialFolderAsync(adversarialFolderId, userId, foreignTenantId, "Original");

        var response = await client.PutAsJsonAsync(
            $"/api/folders/{adversarialFolderId}", new { Name = "Hijacked", ParentFolderId = (Guid?)null });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var folder = await Fixture.QueryDbAsync(db =>
            db.Folders.AsNoTracking().IgnoreQueryFilters().SingleAsync(f => f.Id == adversarialFolderId));
        Assert.Equal("Original", folder.Name);
    }

    [Fact]
    public async Task Delete_adversarial_folder_returns_404_and_leaves_it_in_place()
    {
        var (client, userId, _) = await AuthenticatedOwnerAsync("isolation-delete-a@test.local");
        var (_, _, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-delete-b@test.local");
        var adversarialFolderId = Guid.NewGuid();
        await SeedAdversarialFolderAsync(adversarialFolderId, userId, foreignTenantId, "Untouchable");

        var response = await client.DeleteAsync($"/api/folders/{adversarialFolderId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stillExists = await Fixture.QueryDbAsync(db =>
            db.Folders.AsNoTracking().IgnoreQueryFilters().AnyAsync(f => f.Id == adversarialFolderId));
        Assert.True(stillExists);
    }

    [Fact]
    public async Task Create_document_in_adversarial_folder_returns_400_and_creates_nothing()
    {
        var (client, userId, _) = await AuthenticatedOwnerAsync("isolation-createdoc-a@test.local");
        var (_, _, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-createdoc-b@test.local");
        var adversarialFolderId = Guid.NewGuid();
        await SeedAdversarialFolderAsync(adversarialFolderId, userId, foreignTenantId, "Adversarial folder");

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
    public async Task Download_adversarial_document_returns_404_and_never_calls_file_storage()
    {
        var (client, userId, _) = await AuthenticatedOwnerAsync("isolation-download-a@test.local");
        var (_, _, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-download-b@test.local");
        var adversarialDocumentId = Guid.NewGuid();
        await SeedAdversarialDocumentAsync(adversarialDocumentId, userId, foreignTenantId, "adversarial.txt");

        var response = await client.GetAsync($"/api/documents/{adversarialDocumentId}/download");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, Fixture.FileStorage.DownloadUriCallCount);
    }

    [Fact]
    public async Task CreateShare_on_adversarial_document_returns_404_and_creates_no_share()
    {
        var (client, userId, _) = await AuthenticatedOwnerAsync("isolation-createshare-a@test.local");
        var (_, _, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-createshare-b@test.local");
        var adversarialDocumentId = Guid.NewGuid();
        await SeedAdversarialDocumentAsync(adversarialDocumentId, userId, foreignTenantId, "adversarial.txt");

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
        var (client, userId, _) = await AuthenticatedOwnerAsync("isolation-listshare-a@test.local");
        var (_, _, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-listshare-b@test.local");
        var adversarialDocumentId = Guid.NewGuid();
        await SeedAdversarialDocumentAsync(adversarialDocumentId, userId, foreignTenantId, "adversarial.txt");

        var response = await client.GetAsync($"/api/documents/{adversarialDocumentId}/shares");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteShare_on_adversarial_document_returns_404_and_leaves_share_in_place()
    {
        var (client, userId, _) = await AuthenticatedOwnerAsync("isolation-deleteshare-a@test.local");
        var (_, _, foreignTenantId) = await AuthenticatedOwnerAsync("isolation-deleteshare-b@test.local");
        var adversarialDocumentId = Guid.NewGuid();
        await SeedAdversarialDocumentAsync(adversarialDocumentId, userId, foreignTenantId, "adversarial.txt");
        var adversarialShareId = Guid.NewGuid();
        await SeedAdversarialShareAsync(adversarialShareId, adversarialDocumentId, userId, foreignTenantId);

        var response = await client.DeleteAsync($"/api/documents/{adversarialDocumentId}/shares/{adversarialShareId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stillExists = await Fixture.QueryDbAsync(db =>
            db.DocumentShares.AsNoTracking().IgnoreQueryFilters().AnyAsync(s => s.Id == adversarialShareId));
        Assert.True(stillExists);
    }
}
