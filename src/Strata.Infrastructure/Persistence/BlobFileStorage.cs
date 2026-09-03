using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Strata.Application.Persistence;

namespace Strata.Infrastructure.Persistence;

public class BlobFileStorage : IFileStorage
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName;

    public BlobFileStorage(IConfiguration configuration)
    {
        var serviceUri = new Uri(configuration["BlobStorage:ServiceUri"]!);
        _containerName = configuration["BlobStorage:ContainerName"]!;
        _blobServiceClient = new BlobServiceClient(serviceUri, new DefaultAzureCredential());
    }

    public Task<Uri> GetUploadUriAsync(Guid documentId, string contentType, CancellationToken cancellationToken) =>
        GenerateSasUriAsync(documentId, BlobSasPermissions.Create | BlobSasPermissions.Write, cancellationToken);

    public Task<Uri> GetDownloadUriAsync(Guid documentId, CancellationToken cancellationToken) =>
        GenerateSasUriAsync(documentId, BlobSasPermissions.Read, cancellationToken);

    private async Task<Uri> GenerateSasUriAsync(Guid documentId, BlobSasPermissions permissions, CancellationToken cancellationToken)
    {
        var startsOn = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expiresOn = DateTimeOffset.UtcNow.AddMinutes(15);

        var userDelegationKey = await _blobServiceClient.GetUserDelegationKeyAsync(startsOn, expiresOn, cancellationToken);

        var blobClient = _blobServiceClient
            .GetBlobContainerClient(_containerName)
            .GetBlobClient(documentId.ToString());

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _containerName,
            BlobName = blobClient.Name,
            Resource = "b",
            StartsOn = startsOn,
            ExpiresOn = expiresOn
        };
        sasBuilder.SetPermissions(permissions);

        var sasQueryParameters = sasBuilder.ToSasQueryParameters(userDelegationKey, _blobServiceClient.AccountName);

        var uriBuilder = new UriBuilder(blobClient.Uri) { Query = sasQueryParameters.ToString() };
        return uriBuilder.Uri;
    }
}
