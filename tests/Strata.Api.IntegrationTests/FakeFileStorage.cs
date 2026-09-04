using Strata.Application.Persistence;

namespace Strata.Api.IntegrationTests;

// Never touches real Blob Storage — swapped in for IFileStorage in the test
// host so tests can also assert it was (or wasn't) called at all.
public class FakeFileStorage : IFileStorage
{
    public int UploadUriCallCount { get; private set; }
    public int DownloadUriCallCount { get; private set; }

    public Task<Uri> GetUploadUriAsync(Guid documentId, string contentType, CancellationToken cancellationToken)
    {
        UploadUriCallCount++;
        return Task.FromResult(new Uri($"https://fake-storage.test/upload/{documentId}"));
    }

    public Task<Uri> GetDownloadUriAsync(Guid documentId, CancellationToken cancellationToken)
    {
        DownloadUriCallCount++;
        return Task.FromResult(new Uri($"https://fake-storage.test/download/{documentId}"));
    }

    public void Reset()
    {
        UploadUriCallCount = 0;
        DownloadUriCallCount = 0;
    }
}
