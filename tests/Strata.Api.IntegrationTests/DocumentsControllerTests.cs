using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Strata.Domain.Documents;

namespace Strata.Api.IntegrationTests;

public class DocumentsControllerTests : IntegrationTestBase
{
    public DocumentsControllerTests(IntegrationTestFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Create_with_foreign_folder_returns_400()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-createfolder-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-createfolder-b@test.local");
        var aFolderId = await TestApiHelpers.CreateFolderAsync(clientA, "A's folder");

        var documentCountBefore = await Fixture.QueryDbAsync(db => db.Documents.AsNoTracking().CountAsync());

        var response = await clientB.PostAsJsonAsync("/api/documents", new
        {
            Name = "doc.txt",
            FolderId = aFolderId,
            ContentType = "text/plain",
            Size = 1L
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var documentCountAfter = await Fixture.QueryDbAsync(db => db.Documents.AsNoTracking().CountAsync());
        Assert.Equal(documentCountBefore, documentCountAfter);
        Assert.Equal(0, Fixture.FileStorage.UploadUriCallCount);
    }

    [Fact]
    public async Task Download_of_foreign_document_returns_404_and_never_calls_file_storage()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-download-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-download-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");

        // Creating the document above already called GetUploadUriAsync once —
        // reset the counters so this test only observes the download attempt.
        Fixture.FileStorage.Reset();

        var response = await clientB.GetAsync($"/api/documents/{documentId}/download");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, Fixture.FileStorage.DownloadUriCallCount);
    }

    [Fact]
    public async Task Missing_and_foreign_document_download_produce_the_same_response()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-equiv-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-equiv-b@test.local");
        var foreignDocumentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");

        var foreignResponse = await clientB.GetAsync($"/api/documents/{foreignDocumentId}/download");
        var missingResponse = await clientB.GetAsync($"/api/documents/{Guid.NewGuid()}/download");

        Assert.Equal(foreignResponse.StatusCode, missingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
        Assert.Equal(0, Fixture.FileStorage.DownloadUriCallCount);
    }

    [Fact]
    public async Task Owner_can_download_their_own_document()
    {
        var client = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-owner-download@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(client, "doc.txt");

        var response = await client.GetAsync($"/api/documents/{documentId}/download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, Fixture.FileStorage.DownloadUriCallCount);
    }

    [Fact]
    public async Task Create_share_on_foreign_document_returns_404()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-create-foreign-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-create-foreign-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");

        var response = await clientB.PostAsJsonAsync($"/api/documents/{documentId}/shares",
            new { Email = "shares-create-foreign-b@test.local", Role = DocumentShare.Role.Viewer });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var shareCount = await Fixture.QueryDbAsync(db => db.DocumentShares.AsNoTracking().CountAsync(s => s.DocumentId == documentId));
        Assert.Equal(0, shareCount);
    }

    [Fact]
    public async Task Create_share_with_nonexistent_email_returns_400()
    {
        var client = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-create-noemail@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(client, "doc.txt");

        var response = await client.PostAsJsonAsync($"/api/documents/{documentId}/shares",
            new { Email = "nobody-registered@test.local", Role = DocumentShare.Role.Viewer });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var shareCount = await Fixture.QueryDbAsync(db => db.DocumentShares.AsNoTracking().CountAsync(s => s.DocumentId == documentId));
        Assert.Equal(0, shareCount);
    }

    [Fact]
    public async Task Create_share_with_invalid_role_returns_400()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-create-badrole-a@test.local");
        await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-create-badrole-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");

        var response = await clientA.PostAsJsonAsync($"/api/documents/{documentId}/shares",
            new { Email = "shares-create-badrole-b@test.local", Role = (DocumentShare.Role)999 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var shareCount = await Fixture.QueryDbAsync(db => db.DocumentShares.AsNoTracking().CountAsync(s => s.DocumentId == documentId));
        Assert.Equal(0, shareCount);
    }

    [Fact]
    public async Task Create_share_with_missing_role_returns_400()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-create-norole-a@test.local");
        await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-create-norole-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");

        // No "role" property at all — must not silently bind to the enum's
        // zero value (Member) and grant edit access by omission.
        var response = await clientA.PostAsJsonAsync($"/api/documents/{documentId}/shares",
            new { Email = "shares-create-norole-b@test.local" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var shareCount = await Fixture.QueryDbAsync(db => db.DocumentShares.AsNoTracking().CountAsync(s => s.DocumentId == documentId));
        Assert.Equal(0, shareCount);
    }

    [Fact]
    public async Task Create_share_with_self_returns_400()
    {
        var client = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-create-self@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(client, "doc.txt");

        var response = await client.PostAsJsonAsync($"/api/documents/{documentId}/shares",
            new { Email = "shares-create-self@test.local", Role = DocumentShare.Role.Viewer });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var shareCount = await Fixture.QueryDbAsync(db => db.DocumentShares.AsNoTracking().CountAsync(s => s.DocumentId == documentId));
        Assert.Equal(0, shareCount);
    }

    [Fact]
    public async Task Create_duplicate_share_returns_409()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-duplicate-a@test.local");
        await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-duplicate-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");

        await TestApiHelpers.CreateShareAsync(clientA, documentId, "shares-duplicate-b@test.local", DocumentShare.Role.Viewer);
        var response = await clientA.PostAsJsonAsync($"/api/documents/{documentId}/shares",
            new { Email = "shares-duplicate-b@test.local", Role = DocumentShare.Role.Member });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var shares = await Fixture.QueryDbAsync(db => db.DocumentShares.AsNoTracking().Where(s => s.DocumentId == documentId).ToListAsync());
        var share = Assert.Single(shares);
        Assert.Equal(DocumentShare.Role.Viewer, share.UserRole);
    }

    [Fact]
    public async Task Create_share_grants_recipient_download_access()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-grant-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-grant-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");

        await TestApiHelpers.CreateShareAsync(clientA, documentId, "shares-grant-b@test.local", DocumentShare.Role.Viewer);

        var response = await clientB.GetAsync($"/api/documents/{documentId}/download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, Fixture.FileStorage.DownloadUriCallCount);
    }

    [Fact]
    public async Task List_shares_on_foreign_document_returns_404()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-list-foreign-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-list-foreign-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");

        var response = await clientB.GetAsync($"/api/documents/{documentId}/shares");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_shares_by_recipient_returns_404()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-list-recipient-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-list-recipient-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");
        await TestApiHelpers.CreateShareAsync(clientA, documentId, "shares-list-recipient-b@test.local", DocumentShare.Role.Viewer);

        // Being shared with grants download, not management — listing shares
        // is an owner-only action, same as List_shares_on_foreign_document_returns_404.
        var response = await clientB.GetAsync($"/api/documents/{documentId}/shares");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_shares_returns_created_shares()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-list-a@test.local");
        await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-list-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");
        await TestApiHelpers.CreateShareAsync(clientA, documentId, "shares-list-b@test.local", DocumentShare.Role.Viewer);

        var response = await clientA.GetAsync($"/api/documents/{documentId}/shares");
        var shares = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, shares.GetArrayLength());
    }

    [Fact]
    public async Task Delete_share_on_foreign_document_returns_404()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-delete-foreign-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-delete-foreign-b@test.local");
        await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-delete-foreign-c@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");
        var shareId = await TestApiHelpers.CreateShareAsync(clientA, documentId, "shares-delete-foreign-c@test.local", DocumentShare.Role.Viewer);

        var response = await clientB.DeleteAsync($"/api/documents/{documentId}/shares/{shareId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stillExists = await Fixture.QueryDbAsync(db => db.DocumentShares.AsNoTracking().AnyAsync(s => s.Id == shareId));
        Assert.True(stillExists);
    }

    [Fact]
    public async Task Delete_nonexistent_share_returns_404()
    {
        var client = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-delete-missing@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(client, "doc.txt");

        var response = await client.DeleteAsync($"/api/documents/{documentId}/shares/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_share_revokes_recipient_download_access()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-revoke-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "shares-revoke-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");
        var shareId = await TestApiHelpers.CreateShareAsync(clientA, documentId, "shares-revoke-b@test.local", DocumentShare.Role.Viewer);

        var deleteResponse = await clientA.DeleteAsync($"/api/documents/{documentId}/shares/{shareId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var stillExists = await Fixture.QueryDbAsync(db => db.DocumentShares.AsNoTracking().AnyAsync(s => s.Id == shareId));
        Assert.False(stillExists);

        var downloadResponse = await clientB.GetAsync($"/api/documents/{documentId}/download");
        Assert.Equal(HttpStatusCode.NotFound, downloadResponse.StatusCode);
        Assert.Equal(0, Fixture.FileStorage.DownloadUriCallCount);
    }

    [Fact]
    public async Task Rename_by_owner_succeeds()
    {
        var client = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-rename-owner@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(client, "original.txt");

        var response = await client.PutAsJsonAsync($"/api/documents/{documentId}", new { Name = "renamed.txt" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var document = await Fixture.QueryDbAsync(db => db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId));
        Assert.Equal("renamed.txt", document.Name);
    }

    [Fact]
    public async Task Rename_by_member_succeeds()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-rename-member-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-rename-member-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "original.txt");
        await TestApiHelpers.CreateShareAsync(clientA, documentId, "docs-rename-member-b@test.local", DocumentShare.Role.Member);

        var response = await clientB.PutAsJsonAsync($"/api/documents/{documentId}", new { Name = "renamed-by-member.txt" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var document = await Fixture.QueryDbAsync(db => db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId));
        Assert.Equal("renamed-by-member.txt", document.Name);
    }

    [Fact]
    public async Task Member_can_download()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-member-download-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-member-download-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");
        await TestApiHelpers.CreateShareAsync(clientA, documentId, "docs-member-download-b@test.local", DocumentShare.Role.Member);

        var response = await clientB.GetAsync($"/api/documents/{documentId}/download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, Fixture.FileStorage.DownloadUriCallCount);
    }

    [Fact]
    public async Task Rename_by_viewer_returns_404_and_name_unchanged()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-rename-viewer-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-rename-viewer-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "original.txt");
        await TestApiHelpers.CreateShareAsync(clientA, documentId, "docs-rename-viewer-b@test.local", DocumentShare.Role.Viewer);

        var response = await clientB.PutAsJsonAsync($"/api/documents/{documentId}", new { Name = "Should not persist" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var document = await Fixture.QueryDbAsync(db => db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId));
        Assert.Equal("original.txt", document.Name);
    }

    [Fact]
    public async Task Rename_by_unshared_foreign_user_returns_404_and_name_unchanged()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-rename-foreign-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-rename-foreign-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "original.txt");

        var response = await clientB.PutAsJsonAsync($"/api/documents/{documentId}", new { Name = "Should not persist" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var document = await Fixture.QueryDbAsync(db => db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId));
        Assert.Equal("original.txt", document.Name);
    }

    [Fact]
    public async Task Missing_and_foreign_document_rename_produce_the_same_response()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-rename-equiv-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-rename-equiv-b@test.local");
        var foreignDocumentId = await TestApiHelpers.CreateDocumentAsync(clientA, "original.txt");

        var foreignResponse = await clientB.PutAsJsonAsync($"/api/documents/{foreignDocumentId}", new { Name = "x" });
        var missingResponse = await clientB.PutAsJsonAsync($"/api/documents/{Guid.NewGuid()}", new { Name = "x" });

        Assert.Equal(foreignResponse.StatusCode, missingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
    }

    [Fact]
    public async Task Viewer_can_still_download_despite_no_rename_access()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-viewer-download-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-viewer-download-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "original.txt");
        await TestApiHelpers.CreateShareAsync(clientA, documentId, "docs-viewer-download-b@test.local", DocumentShare.Role.Viewer);

        var renameResponse = await clientB.PutAsJsonAsync($"/api/documents/{documentId}", new { Name = "Should not persist" });
        Assert.Equal(HttpStatusCode.NotFound, renameResponse.StatusCode);

        var downloadResponse = await clientB.GetAsync($"/api/documents/{documentId}/download");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
    }

    [Fact]
    public async Task Member_cannot_create_shares()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-member-createshare-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-member-createshare-b@test.local");
        await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-member-createshare-c@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");
        await TestApiHelpers.CreateShareAsync(clientA, documentId, "docs-member-createshare-b@test.local", DocumentShare.Role.Member);

        var response = await clientB.PostAsJsonAsync($"/api/documents/{documentId}/shares",
            new { Email = "docs-member-createshare-c@test.local", Role = DocumentShare.Role.Viewer });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var shareCount = await Fixture.QueryDbAsync(db => db.DocumentShares.AsNoTracking().CountAsync(s => s.DocumentId == documentId));
        Assert.Equal(1, shareCount);
    }

    [Fact]
    public async Task Member_cannot_list_shares()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-member-listshare-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-member-listshare-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");
        await TestApiHelpers.CreateShareAsync(clientA, documentId, "docs-member-listshare-b@test.local", DocumentShare.Role.Member);

        var response = await clientB.GetAsync($"/api/documents/{documentId}/shares");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Member_cannot_delete_shares()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-member-deleteshare-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-member-deleteshare-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "doc.txt");
        var shareId = await TestApiHelpers.CreateShareAsync(clientA, documentId, "docs-member-deleteshare-b@test.local", DocumentShare.Role.Member);

        var response = await clientB.DeleteAsync($"/api/documents/{documentId}/shares/{shareId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stillExists = await Fixture.QueryDbAsync(db => db.DocumentShares.AsNoTracking().AnyAsync(s => s.Id == shareId));
        Assert.True(stillExists);
    }

    [Fact]
    public async Task Create_document_stores_authenticated_tenant_id()
    {
        var client = Fixture.Factory.CreateClient();
        var token = await TestApiHelpers.RegisterAsync(client, "docs-tenant-create@test.local");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var expectedTenantId = TestApiHelpers.TenantIdFromToken(token);

        var documentId = await TestApiHelpers.CreateDocumentAsync(client, "tenant-doc.txt");

        var document = await Fixture.QueryDbAsync(db => db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId));
        Assert.Equal(expectedTenantId, document.TenantId);
    }

    [Fact]
    public async Task Create_document_ignores_client_supplied_tenant_id()
    {
        var client = Fixture.Factory.CreateClient();
        var token = await TestApiHelpers.RegisterAsync(client, "docs-tenant-crafted@test.local");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var expectedTenantId = TestApiHelpers.TenantIdFromToken(token);
        var craftedTenantId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/api/documents", new
        {
            Name = "crafted-doc.txt",
            FolderId = (Guid?)null,
            ContentType = "text/plain",
            Size = 1L,
            tenantId = craftedTenantId
        });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = json.GetProperty("documentId").GetGuid();

        var document = await Fixture.QueryDbAsync(db => db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId));
        Assert.Equal(expectedTenantId, document.TenantId);
        Assert.NotEqual(craftedTenantId, document.TenantId);
    }

    [Fact]
    public async Task Create_share_stores_documents_tenant_id_not_recipients()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-share-tenant-a@test.local");
        await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "docs-share-tenant-b@test.local");
        var documentId = await TestApiHelpers.CreateDocumentAsync(clientA, "shared-doc.txt");

        var shareId = await TestApiHelpers.CreateShareAsync(clientA, documentId, "docs-share-tenant-b@test.local", DocumentShare.Role.Viewer);

        var document = await Fixture.QueryDbAsync(db => db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId));
        var share = await Fixture.QueryDbAsync(db => db.DocumentShares.AsNoTracking().SingleAsync(s => s.Id == shareId));

        // The recipient (client B) is a different tenant than the document's
        // owner (client A) — every AuthenticatedClientAsync call registers a
        // brand-new tenant. The share must still carry the document's
        // tenant, not the recipient's.
        Assert.Equal(document.TenantId, share.TenantId);
    }
}
