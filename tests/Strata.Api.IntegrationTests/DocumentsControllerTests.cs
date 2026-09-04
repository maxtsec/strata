using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

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
}
