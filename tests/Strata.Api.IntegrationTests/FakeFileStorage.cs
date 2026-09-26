using Strata.Application.Persistence;

namespace Strata.Api.IntegrationTests;

// Never touches real Blob Storage — swapped in for IFileStorage in the test
// host so tests can also assert it was (or wasn't) called at all.
public class FakeFileStorage : IFileStorage
{
    public int UploadUriCallCount { get; private set; }
    public int DownloadUriCallCount { get; private set; }
    public int ValidationCallCount { get; private set; }
    public UploadValidationStatus ValidationStatus { get; set; } = UploadValidationStatus.Valid;
    public long? LastExpectedSize { get; private set; }
    public string? LastExpectedContentType { get; private set; }

    public Task<Uri> GetUploadUriAsync(Guid documentId, CancellationToken cancellationToken)
    {
        UploadUriCallCount++;
        return Task.FromResult(new Uri($"https://fake-storage.test/upload/{documentId}"));
    }

    public Task<ValidatedDownloadUri> GetValidatedDownloadUriAsync(
        Guid documentId, long expectedSize, string expectedContentType, CancellationToken cancellationToken)
    {
        ValidationCallCount++;
        LastExpectedSize = expectedSize;
        LastExpectedContentType = expectedContentType;
        if (ValidationStatus != UploadValidationStatus.Valid)
        {
            return Task.FromResult(new ValidatedDownloadUri(ValidationStatus));
        }

        DownloadUriCallCount++;
        return Task.FromResult(new ValidatedDownloadUri(
            UploadValidationStatus.Valid, new Uri($"https://fake-storage.test/download/{documentId}")));
    }

    public void Reset()
    {
        UploadUriCallCount = 0;
        DownloadUriCallCount = 0;
        ValidationCallCount = 0;
        ValidationStatus = UploadValidationStatus.Valid;
        LastExpectedSize = null;
        LastExpectedContentType = null;
    }
}
