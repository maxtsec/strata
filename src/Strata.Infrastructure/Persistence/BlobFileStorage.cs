using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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

    // A create-only SAS permits the first Put Blob, but cannot overwrite it
    // after validation. Clients upload one block blob with Put Blob.
    public Task<Uri> GetUploadUriAsync(Guid documentId, CancellationToken cancellationToken) =>
        GenerateSasUriAsync(documentId, BlobSasPermissions.Create, null, cancellationToken);

    public async Task<ValidatedDownloadUri> GetValidatedDownloadUriAsync(
        Guid documentId, long expectedSize, string expectedContentType, CancellationToken cancellationToken)
    {
        if (!DocumentUploadPolicy.IsValidSize(expectedSize) ||
            !DocumentUploadPolicy.TryNormalizeContentType(expectedContentType, out var contentType))
        {
            return new ValidatedDownloadUri(UploadValidationStatus.Invalid);
        }

        var blobClient = _blobServiceClient
            .GetBlobContainerClient(_containerName)
            .GetBlobClient(documentId.ToString());

        BlobProperties properties;
        try
        {
            properties = (await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return new ValidatedDownloadUri(UploadValidationStatus.Missing);
        }

        if (properties.BlobType != BlobType.Block ||
            properties.ContentLength != expectedSize ||
            !string.Equals(properties.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
        {
            return new ValidatedDownloadUri(UploadValidationStatus.Invalid);
        }

        try
        {
            var options = new BlobDownloadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = properties.ETag }
            };
            if (contentType != "text/plain")
            {
                options.Range = new HttpRange(0, Math.Min(properties.ContentLength, 8));
            }

            var download = await blobClient.DownloadStreamingAsync(options, cancellationToken);

            await using var content = download.Value.Content;
            if (!await DocumentUploadPolicy.ContentMatchesTypeAsync(content, contentType, cancellationToken))
            {
                return new ValidatedDownloadUri(UploadValidationStatus.Invalid);
            }
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return new ValidatedDownloadUri(UploadValidationStatus.Missing);
        }
        catch (RequestFailedException exception) when (exception.Status == 412)
        {
            // The blob changed between the property and byte checks, so no
            // read SAS may be issued for that version.
            return new ValidatedDownloadUri(UploadValidationStatus.Invalid);
        }

        var uri = await GenerateSasUriAsync(documentId, BlobSasPermissions.Read, contentType, cancellationToken);
        return new ValidatedDownloadUri(UploadValidationStatus.Valid, uri);
    }

    private async Task<Uri> GenerateSasUriAsync(
        Guid documentId, BlobSasPermissions permissions, string? contentType, CancellationToken cancellationToken)
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
        if (permissions == BlobSasPermissions.Read)
        {
            sasBuilder.ContentType = contentType;
            sasBuilder.ContentDisposition = "attachment";
        }

        var sasQueryParameters = sasBuilder.ToSasQueryParameters(userDelegationKey, _blobServiceClient.AccountName);

        var uriBuilder = new UriBuilder(blobClient.Uri) { Query = sasQueryParameters.ToString() };
        return uriBuilder.Uri;
    }
}
