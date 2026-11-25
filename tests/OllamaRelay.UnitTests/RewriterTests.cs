using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using OllamaRelay.Relay.Inference;

namespace OllamaRelay.UnitTests;

public class RewriterTests
{
    private static readonly EndpointSpec NativeChat =
        InferenceEndpoints.Forwarded.Single(e => e.Path == "/api/chat");

    private static readonly EndpointSpec OpenAiChat =
        InferenceEndpoints.Forwarded.Single(e => e.Path == "/v1/chat/completions");

    [Theory]
    [InlineData("lit-writer", "lit-writer")]
    [InlineData("lit-writer:latest", "lit-writer")]
    [InlineData(" Lit-Writer ", "lit-writer")]
    [InlineData("a", "a")]
    [InlineData("lit-writer:7b", null)]
    [InlineData("hf.co/x/y:Q4", null)]
    [InlineData("", null)]
    [InlineData("-bad", null)]
    [InlineData(null, null)]
    public void Alias_normalization(string? input, string? expected) =>
        Assert.Equal(expected, ModelNames.Normalize(input));

    [Fact]
    public void Alias_pattern_limits_length()
    {
        Assert.True(ModelNames.IsValidAlias(new string('a', 63)));
        Assert.False(ModelNames.IsValidAlias(new string('a', 64)));
        Assert.False(ModelNames.IsValidAlias("UPPER"));
    }

    [Fact]
    public void Request_rewrite_sets_upstream_and_strips_keep_alive()
    {
        var body = RequestRewriter.TryParse("""{"model":"alias","keep_alive":-1,"messages":[{"role":"user","content":"hi ü"}]}"""u8)!;
        var bytes = RequestRewriter.Rewrite(body, "hf.co/x/Long:Q4_K_M", NativeChat, stripKeepAlive: true);
        var result = JsonNode.Parse(bytes)!.AsObject();

        Assert.Equal("hf.co/x/Long:Q4_K_M", (string?)result["model"]);
        Assert.False(result.ContainsKey("keep_alive"));
        Assert.Equal("hi ü", (string?)result["messages"]![0]!["content"]);
    }

    [Fact]
    public void Request_rewrite_keeps_keep_alive_when_disabled()
    {
        var body = RequestRewriter.TryParse("""{"model":"alias","keep_alive":"5m"}"""u8)!;
        var result = JsonNode.Parse(RequestRewriter.Rewrite(body, "up", NativeChat, stripKeepAlive: false))!;
        Assert.Equal("5m", (string?)result["keep_alive"]);
    }

    [Fact]
    public void OpenAi_stream_requests_get_usage_enabled()
    {
        var body = RequestRewriter.TryParse("""{"model":"alias","stream":true,"stream_options":{"foo":1}}"""u8)!;
        var result = JsonNode.Parse(RequestRewriter.Rewrite(body, "up", OpenAiChat, true))!;
        Assert.True((bool)result["stream_options"]!["include_usage"]!);
        Assert.Equal(1, (int)result["stream_options"]!["foo"]!);

        var plain = RequestRewriter.TryParse("""{"model":"alias"}"""u8)!;
        Assert.Null(JsonNode.Parse(RequestRewriter.Rewrite(plain, "up", OpenAiChat, true))!["stream_options"]);
    }

    [Fact]
    public void Non_object_bodies_are_rejected()
    {
        Assert.Null(RequestRewriter.TryParse("[1,2]"u8));
        Assert.Null(RequestRewriter.TryParse("nope"u8));
    }

    private static string Run(RewriteMode mode, string alias, params string[] chunks) =>
        Run(new ResponseRewriter(mode, alias, 1024 * 1024), chunks);

    private static string Run(ResponseRewriter rewriter, params string[] chunks)
    {
        var output = new ArrayBufferWriter<byte>();
        foreach (var chunk in chunks)
        {
            rewriter.Process(Encoding.UTF8.GetBytes(chunk), output);
        }

        rewriter.Complete(output);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    [Fact]
    public void Ndjson_lines_are_rewritten_across_chunk_boundaries()
    {
        var rewriter = new ResponseRewriter(RewriteMode.Ndjson, "alias", 1024 * 1024);
        var text = Run(rewriter,
            """{"model":"up:7b","message":{"content":"Hel""",
            """lo"},"done":false}""" + "\n" + """{"model":"up:7b","done":true,""",
            "\"prompt_eval_count\":12,\"eval_count\":34}\n");

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Equal("alias", (string?)JsonNode.Parse(l)!["model"]));
        Assert.Equal("Hello", (string?)JsonNode.Parse(lines[0])!["message"]!["content"]);
        Assert.Equal(12, rewriter.PromptTokens);
        Assert.Equal(34, rewriter.CompletionTokens);
    }

    [Fact]
    public void Sse_data_lines_are_rewritten_and_done_passes()
    {
        var rewriter = new ResponseRewriter(RewriteMode.Sse, "alias", 1024 * 1024);
        var text = Run(rewriter,
            "data: {\"id\":\"1\",\"model\":\"up\",\"choices\":[]}\n\n",
            "data: {\"model\":\"up\",\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":7}}\n\n",
            "data: [DONE]\n\n");

        Assert.DoesNotContain("\"up\"", text);
        Assert.Contains("data: [DONE]\n\n", text);
        Assert.Equal(3, text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(5, rewriter.PromptTokens);
        Assert.Equal(7, rewriter.CompletionTokens);
    }

    [Fact]
    public void Responses_api_nested_model_and_usage()
    {
        var rewriter = new ResponseRewriter(RewriteMode.Sse, "alias", 1024 * 1024);
        var text = Run(rewriter,
            "event: response.completed\n",
            "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"up\",\"usage\":{\"input_tokens\":3,\"output_tokens\":4}}}\n\n");
        Assert.Contains("event: response.completed\n", text);
        Assert.Contains("\"model\":\"alias\"", text);
        Assert.Equal(3, rewriter.PromptTokens);
        Assert.Equal(4, rewriter.CompletionTokens);
    }

    [Fact]
    public void Json_body_is_rewritten_at_the_end()
    {
        var rewriter = new ResponseRewriter(RewriteMode.Json, "alias", 1024 * 1024);
        var text = Run(rewriter, """{"model":"up","embeddings":[[0.1,""", """0.2]],"prompt_eval_count":9}""");
        var json = JsonNode.Parse(text)!;
        Assert.Equal("alias", (string?)json["model"]);
        Assert.Equal(0.2, (double)json["embeddings"]![0]![1]!);
        Assert.Equal(9, rewriter.PromptTokens);
    }

    [Fact]
    public void Oversized_json_passes_through_unmodified()
    {
        var rewriter = new ResponseRewriter(RewriteMode.Json, "alias", 16);
        var text = Run(rewriter, "{\"model\":\"up\",", "\"data\":\"0123456789\"}");
        Assert.Equal("""{"model":"up","data":"0123456789"}""", text);
    }

    [Fact]
    public void Errors_and_unknown_types_pass_through()
    {
        Assert.Equal(RewriteMode.Passthrough, ResponseRewriter.ModeFor(500, "application/json"));
        Assert.Equal(RewriteMode.Passthrough, ResponseRewriter.ModeFor(200, "text/plain"));
        Assert.Equal(RewriteMode.Ndjson, ResponseRewriter.ModeFor(200, "application/x-ndjson"));
        Assert.Equal(RewriteMode.Sse, ResponseRewriter.ModeFor(200, "text/event-stream; charset=utf-8"));
        Assert.Equal(RewriteMode.Json, ResponseRewriter.ModeFor(200, "application/json; charset=utf-8"));
        Assert.Equal("{\"error\":\"x\"}", Run(RewriteMode.Passthrough, "alias", "{\"error\":\"x\"}"));
    }

    [Fact]
    public void Non_json_lines_are_kept()
    {
        Assert.Equal("garbage\n{\"model\":\"alias\"}\n",
            Run(RewriteMode.Ndjson, "alias", "garbage\n", "{\"model\":\"up\"}\n"));
    }
}
