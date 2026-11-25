namespace OllamaRelay.Node;

/// <summary>
/// The node's own list of upstream calls it is willing to make. This duplicates the relay's
/// allowlist on purpose: a compromised relay must not be able to reach model management
/// endpoints (pull, delete, create, push, copy) or any other host.
/// </summary>
public static class NodeAllowlist
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "POST /api/chat",
        "POST /api/generate",
        "POST /api/embed",
        "POST /api/embeddings",
        "POST /api/show",
        "POST /v1/chat/completions",
        "POST /v1/completions",
        "POST /v1/embeddings",
        "POST /v1/responses",
    };

    /// <summary>
    /// True only for an exact method and path from the list. Query strings, fragments,
    /// encoded characters, dot segments and absolute URLs are all rejected.
    /// </summary>
    public static bool IsAllowed(string method, string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return false;
        }

        foreach (var c in path)
        {
            if (c is '?' or '#' or '%' or '\\' || char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return false;
            }
        }

        if (path.Contains("//", StringComparison.Ordinal) || path.Contains("/.", StringComparison.Ordinal))
        {
            return false;
        }

        return Allowed.Contains($"{method} {path}");
    }
}
