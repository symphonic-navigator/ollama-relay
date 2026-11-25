using System.Text.RegularExpressions;

namespace OllamaRelay.Relay.Inference;

public static partial class ModelNames
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}$")]
    private static partial Regex AliasPattern();

    public static bool IsValidAlias(string? alias) => alias is not null && AliasPattern().IsMatch(alias);

    /// <summary>
    /// Maps what a client sends to an alias: trims, lowercases and drops a trailing <c>:latest</c>,
    /// because Ollama clients like to append it. Returns null for anything that cannot be an alias.
    /// </summary>
    public static string? Normalize(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        var name = requested.Trim().ToLowerInvariant();
        if (name.EndsWith(":latest", StringComparison.Ordinal))
        {
            name = name[..^":latest".Length];
        }

        return IsValidAlias(name) ? name : null;
    }
}
