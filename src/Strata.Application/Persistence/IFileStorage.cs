namespace Strata.Application.Persistence;

public interface IFileStorage
{
    Task<Uri> GetUploadUriAsync(Guid documentId, string contentType, CancellationToken cancellationToken);

    Task<Uri> GetDownloadUriAsync(Guid documentId, CancellationToken cancellationToken);
}