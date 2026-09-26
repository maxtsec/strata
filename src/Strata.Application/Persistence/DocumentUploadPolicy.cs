using System.Text;

namespace Strata.Application.Persistence;

public static class DocumentUploadPolicy
{
    public const long MaxSizeBytes = 25_000_000;

    private static readonly HashSet<string> SupportedContentTypes =
    [
        "application/pdf",
        "text/plain",
        "image/png",
        "image/jpeg"
    ];

    public static bool IsValidSize(long size) => size is > 0 and <= MaxSizeBytes;

    public static bool TryNormalizeContentType(string? contentType, out string normalized)
    {
        normalized = contentType?.Trim().ToLowerInvariant() ?? string.Empty;
        return SupportedContentTypes.Contains(normalized);
    }

    public static async Task<bool> ContentMatchesTypeAsync(
        Stream content, string contentType, CancellationToken cancellationToken)
    {
        if (contentType == "text/plain")
        {
            try
            {
                using var reader = new StreamReader(
                    content, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false,
                    bufferSize: 8192, leaveOpen: true);
                var buffer = new char[8192];
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var character = buffer[i];
                        if (char.IsControl(character) && character is not ('\t' or '\n' or '\r'))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        var signature = contentType switch
        {
            "application/pdf" => "%PDF-"u8.ToArray(),
            "image/png" => new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            "image/jpeg" => new byte[] { 255, 216, 255 },
            _ => []
        };

        if (signature.Length == 0)
        {
            return false;
        }

        var prefix = new byte[signature.Length];
        var bytesRead = 0;
        while (bytesRead < prefix.Length)
        {
            var read = await content.ReadAsync(prefix.AsMemory(bytesRead), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            bytesRead += read;
        }

        return prefix.AsSpan().SequenceEqual(signature);
    }
}
