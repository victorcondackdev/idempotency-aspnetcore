using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Payments.Idempotency.AspNetCore.Internal;

/// <summary>Computes the SHA-256 fingerprint of a request: method, path, optional query and headers, and body.</summary>
internal static class RequestFingerprint
{
    private const int BufferSize = 16 * 1024;

    public static async Task<string> ComputeAsync(HttpRequest request, IdempotencyOptions options, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendField(hash, request.Method);
        AppendField(hash, (request.PathBase.Value ?? string.Empty) + (request.Path.Value ?? string.Empty));
        if (options.IncludeQueryStringInFingerprint)
        {
            AppendField(hash, request.QueryString.Value ?? string.Empty);
        }

        foreach (var name in options.FingerprintHeaders)
        {
            AppendField(hash, name.ToLowerInvariant() + ":" + request.Headers[name].ToString());
        }

        // Buffer the body so the endpoint can still read it after hashing. Large bodies spill to
        // disk (ASP.NET Core's FileBufferingReadStream), so this does not hold them in memory.
        request.EnableBuffering();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        request.Body.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Length-prefixed so that ("ab", "c") and ("a", "bc") never collide.
    private static void AppendField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
