using OllamaRelay.Node;

namespace OllamaRelay.UnitTests;

public class NodeTests
{
    [Theory]
    [InlineData("POST", "/api/chat")]
    [InlineData("POST", "/api/generate")]
    [InlineData("POST", "/api/embed")]
    [InlineData("POST", "/api/embeddings")]
    [InlineData("POST", "/api/show")]
    [InlineData("POST", "/v1/chat/completions")]
    [InlineData("POST", "/v1/completions")]
    [InlineData("POST", "/v1/embeddings")]
    [InlineData("POST", "/v1/responses")]
    public void Allowlist_accepts_inference_endpoints(string method, string path) =>
        Assert.True(NodeAllowlist.IsAllowed(method, path));

    [Theory]
    [InlineData("POST", "/api/pull")]
    [InlineData("DELETE", "/api/delete")]
    [InlineData("POST", "/api/create")]
    [InlineData("POST", "/api/push")]
    [InlineData("POST", "/api/copy")]
    [InlineData("GET", "/api/chat")]
    [InlineData("POST", "/api/chat?x=1")]
    [InlineData("POST", "/api/chat#x")]
    [InlineData("POST", "/api/%63hat")]
    [InlineData("POST", "/api/../api/pull")]
    [InlineData("POST", "/api/./chat")]
    [InlineData("POST", "//evil.example/api/chat")]
    [InlineData("POST", "http://evil.example/api/chat")]
    [InlineData("POST", "api/chat")]
    [InlineData("POST", "")]
    [InlineData("POST", "/API/CHAT")]
    [InlineData("POST", "/api/chat/")]
    public void Allowlist_rejects_everything_else(string method, string path) =>
        Assert.False(NodeAllowlist.IsAllowed(method, path));

    [Fact]
    public void Relay_url_must_be_wss_unless_insecure_is_explicit()
    {
        Assert.Throws<InvalidOperationException>(() => NodeOptions.Validate(new Uri("ws://relay"), false));
        Assert.Throws<InvalidOperationException>(() => NodeOptions.Validate(new Uri("https://relay"), true));
        NodeOptions.Validate(new Uri("wss://relay"), false);
        NodeOptions.Validate(new Uri("ws://relay"), true);
    }

    [Theory]
    [InlineData("wss://relay.example.net", "wss://relay.example.net/node/connect")]
    [InlineData("wss://relay.example.net/", "wss://relay.example.net/node/connect")]
    [InlineData("wss://relay.example.net/node/connect", "wss://relay.example.net/node/connect")]
    [InlineData("wss://example.net/relay", "wss://example.net/relay/node/connect")]
    public void Relay_url_is_normalized(string input, string expected) =>
        Assert.Equal(expected, NodeOptions.NormalizeRelayUrl(new Uri(input)).ToString());

    [Fact]
    public void Backoff_grows_and_is_capped()
    {
        var min = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(60);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var d = Backoff.Compute(attempt, min, max);
            Assert.InRange(d, min, max);
            Assert.True(d.TotalMilliseconds <= Math.Max(min.TotalMilliseconds, 1000 * Math.Pow(2, attempt)));
        }
    }
}
