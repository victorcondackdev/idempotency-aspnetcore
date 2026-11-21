using System.Security.Cryptography;
using System.Text;

namespace Payments.Idempotency.AspNetCore.Internal;

/// <summary>Builds the store key: <c>{prefix}:{sha256(scope, method, route, key)}</c>.</summary>
internal static class StoreKey
{
    public static string Create(string prefix, string scope, string method, string route, string idempotencyKey)
    {
        var builder = new StringBuilder();
        foreach (var part in new[] { scope, method, route, idempotencyKey })
        {
            // Length-prefixed so that no choice of parts can collide with another.
            builder.Append(part.Length).Append(':').Append(part).Append('|');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return prefix + ":" + Convert.ToHexString(digest);
    }
}
