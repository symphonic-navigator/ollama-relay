using System.Security.Cryptography;
using System.Text;

namespace OllamaRelay.Relay.Security;

/// <summary>
/// High entropy secrets (API keys, node tokens, invite and refresh tokens). Only SHA-256 hashes are stored:
/// the secrets carry 256 random bits, so a slow hash would add latency without adding safety.
/// </summary>
public static class SecretTokens
{
    public const string ApiKeyPrefix = "orl_";
    public const string NodeTokenPrefix = "orn_";

    public sealed record Issued(string Secret, string Prefix, string Hash);

    /// <summary>Format <c>orl_&lt;8 hex prefix&gt;_&lt;64 hex secret&gt;</c>.</summary>
    public static Issued NewApiKey() => NewPrefixed(ApiKeyPrefix);

    public static Issued NewNodeToken() => NewPrefixed(NodeTokenPrefix);

    private static Issued NewPrefixed(string kind)
    {
        var prefix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var full = $"{kind}{prefix}_{secret}";
        return new Issued(full, $"{kind}{prefix}", Hash(full));
    }

    /// <summary>URL safe random token for invite, reset and refresh tokens.</summary>
    public static string NewOpaque() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Cheap shape check before touching the database.</summary>
    public static bool LooksLike(string? value, string kind) =>
        value is { Length: > 20 and < 200 } && value.StartsWith(kind, StringComparison.Ordinal);
}
