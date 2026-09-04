using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Strata.Api.IntegrationTests;

public class FoldersControllerTests : IntegrationTestBase
{
    public FoldersControllerTests(IntegrationTestFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task List_only_returns_current_users_folders()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-list-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-list-b@test.local");

        await TestApiHelpers.CreateFolderAsync(clientA, "A's folder");

        var response = await clientB.GetAsync("/api/folders");
        var folders = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, folders.GetArrayLength());
    }

    [Fact]
    public async Task Update_on_foreign_folder_returns_404()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-update-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-update-b@test.local");
        var folderId = await TestApiHelpers.CreateFolderAsync(clientA, "A's folder");

        var response = await clientB.PutAsJsonAsync($"/api/folders/{folderId}", new { Name = "Hijacked", ParentFolderId = (Guid?)null });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var folder = await Fixture.QueryDbAsync(db =>
            db.Folders.AsNoTracking().SingleAsync(f => f.Id == folderId));
        Assert.Equal("A's folder", folder.Name);
        Assert.Null(folder.ParentFolderId);
    }

    [Fact]
    public async Task Delete_on_foreign_folder_returns_404()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-delete-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-delete-b@test.local");
        var folderId = await TestApiHelpers.CreateFolderAsync(clientA, "A's folder");

        var response = await clientB.DeleteAsync($"/api/folders/{folderId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stillExists = await Fixture.QueryDbAsync(db =>
            db.Folders.AsNoTracking().AnyAsync(f => f.Id == folderId));
        Assert.True(stillExists);
    }

    [Fact]
    public async Task Missing_and_foreign_folder_produce_the_same_response_on_update()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-equiv-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-equiv-b@test.local");
        var foreignFolderId = await TestApiHelpers.CreateFolderAsync(clientA, "A's folder");

        var foreignResponse = await clientB.PutAsJsonAsync(
            $"/api/folders/{foreignFolderId}", new { Name = "x", ParentFolderId = (Guid?)null });
        var missingResponse = await clientB.PutAsJsonAsync(
            $"/api/folders/{Guid.NewGuid()}", new { Name = "x", ParentFolderId = (Guid?)null });

        Assert.Equal(foreignResponse.StatusCode, missingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
    }

    [Fact]
    public async Task Create_with_foreign_parent_returns_400()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-createparent-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-createparent-b@test.local");
        var aFolderId = await TestApiHelpers.CreateFolderAsync(clientA, "A's folder");

        var folderCountBefore = await Fixture.QueryDbAsync(db => db.Folders.AsNoTracking().CountAsync());

        var response = await clientB.PostAsJsonAsync("/api/folders", new { Name = "B's folder", ParentFolderId = aFolderId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var folderCountAfter = await Fixture.QueryDbAsync(db => db.Folders.AsNoTracking().CountAsync());
        Assert.Equal(folderCountBefore, folderCountAfter);
    }

    [Fact]
    public async Task Update_with_foreign_parent_returns_400()
    {
        var clientA = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-updateparent-a@test.local");
        var clientB = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-updateparent-b@test.local");
        var aFolderId = await TestApiHelpers.CreateFolderAsync(clientA, "A's folder");
        var bFolderId = await TestApiHelpers.CreateFolderAsync(clientB, "B's folder");

        var response = await clientB.PutAsJsonAsync($"/api/folders/{bFolderId}", new { Name = "B's folder", ParentFolderId = aFolderId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var bFolder = await Fixture.QueryDbAsync(db =>
            db.Folders.AsNoTracking().SingleAsync(f => f.Id == bFolderId));
        Assert.Equal("B's folder", bFolder.Name);
        Assert.Null(bFolder.ParentFolderId);
    }

    [Fact]
    public async Task Direct_self_parent_is_rejected()
    {
        var client = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-selfparent@test.local");
        var folderId = await TestApiHelpers.CreateFolderAsync(client, "Folder");

        var response = await client.PutAsJsonAsync($"/api/folders/{folderId}", new { Name = "Folder", ParentFolderId = folderId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var folder = await Fixture.QueryDbAsync(db =>
            db.Folders.AsNoTracking().SingleAsync(f => f.Id == folderId));
        Assert.Null(folder.ParentFolderId);
    }

    [Fact]
    public async Task Indirect_cycle_is_rejected()
    {
        var client = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-cycle@test.local");
        var aId = await TestApiHelpers.CreateFolderAsync(client, "A");
        var bId = await TestApiHelpers.CreateFolderAsync(client, "B", aId);
        var cId = await TestApiHelpers.CreateFolderAsync(client, "C", bId);

        // A -> B -> C already exists; try to also set A's parent to C, closing the loop.
        var response = await client.PutAsJsonAsync($"/api/folders/{aId}", new { Name = "A", ParentFolderId = cId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var folderA = await Fixture.QueryDbAsync(db =>
            db.Folders.AsNoTracking().SingleAsync(f => f.Id == aId));
        Assert.Null(folderA.ParentFolderId);
    }

    [Fact]
    public async Task Delete_folder_with_child_folder_returns_409()
    {
        var client = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-nonempty-child@test.local");
        var parentId = await TestApiHelpers.CreateFolderAsync(client, "Parent");
        var childId = await TestApiHelpers.CreateFolderAsync(client, "Child", parentId);

        var response = await client.DeleteAsync($"/api/folders/{parentId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var parentExists = await Fixture.QueryDbAsync(db => db.Folders.AsNoTracking().AnyAsync(f => f.Id == parentId));
        var childExists = await Fixture.QueryDbAsync(db => db.Folders.AsNoTracking().AnyAsync(f => f.Id == childId));
        Assert.True(parentExists);
        Assert.True(childExists);
    }

    [Fact]
    public async Task Delete_folder_with_document_returns_409()
    {
        var client = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-nonempty-doc@test.local");
        var folderId = await TestApiHelpers.CreateFolderAsync(client, "Folder");
        var documentId = await TestApiHelpers.CreateDocumentAsync(client, "doc.txt", folderId);

        var response = await client.DeleteAsync($"/api/folders/{folderId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var folderExists = await Fixture.QueryDbAsync(db => db.Folders.AsNoTracking().AnyAsync(f => f.Id == folderId));
        var documentExists = await Fixture.QueryDbAsync(db => db.Documents.AsNoTracking().AnyAsync(d => d.Id == documentId));
        Assert.True(folderExists);
        Assert.True(documentExists);
    }

    [Fact]
    public async Task Delete_empty_folder_succeeds()
    {
        var client = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "folders-empty-delete@test.local");
        var folderId = await TestApiHelpers.CreateFolderAsync(client, "Folder");

        var response = await client.DeleteAsync($"/api/folders/{folderId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
