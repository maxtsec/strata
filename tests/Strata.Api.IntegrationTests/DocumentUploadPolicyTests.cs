using System.Text;
using Strata.Application.Persistence;

namespace Strata.Api.IntegrationTests;

public class DocumentUploadPolicyTests
{
    [Theory]
    [InlineData("application/pdf", "%PDF-1.7", true)]
    [InlineData("application/pdf", "not-a-pdf", false)]
    [InlineData("text/plain", "hello\nworld", true)]
    [InlineData("image/png", "not-a-png", false)]
    public async Task Content_validation_checks_declared_type(string contentType, string content, bool expected)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var matches = await DocumentUploadPolicy.ContentMatchesTypeAsync(stream, contentType, CancellationToken.None);

        Assert.Equal(expected, matches);
    }

    [Theory]
    [InlineData("image/png", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1 }, true)]
    [InlineData("image/jpeg", new byte[] { 255, 216, 255, 224 }, true)]
    [InlineData("image/jpeg", new byte[] { 255, 216, 0, 224 }, false)]
    [InlineData("text/plain", new byte[] { 0xC3, 0x28 }, false)]
    [InlineData("text/plain", new byte[] { 65, 0, 66 }, false)]
    public async Task Content_validation_checks_binary_signatures_and_utf8(string contentType, byte[] bytes, bool expected)
    {
        await using var stream = new MemoryStream(bytes);

        var matches = await DocumentUploadPolicy.ContentMatchesTypeAsync(stream, contentType, CancellationToken.None);

        Assert.Equal(expected, matches);
    }
}
