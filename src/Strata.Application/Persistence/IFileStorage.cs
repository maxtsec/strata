namespace Strata.Application.Persistence;

public interface IFileStorage
{
    Task<Uri> GetUploadUriAsync(Guid documentId, CancellationToken cancellationToken);

    Task<ValidatedDownloadUri> GetValidatedDownloadUriAsync(
        Guid documentId, long expectedSize, string expectedContentType, CancellationToken cancellationToken);
}

public enum UploadValidationStatus
{
    Valid,
    Missing,
    Invalid
}

public sealed record ValidatedDownloadUri(UploadValidationStatus Status, Uri? Uri = null);
