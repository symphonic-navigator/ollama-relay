using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OllamaRelay.Protocol;

namespace OllamaRelay.Node;

/// <summary>Talks to the local Ollama for status calls. Inference calls go through <see cref="NodeStream"/>.</summary>
public sealed class OllamaClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpClient Http => http;

    public async Task<HeartbeatMessage> GetStatusAsync(int activeStreams, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var version = await http.GetFromJsonAsync<VersionDto>("/api/version", Json, timeout.Token).ConfigureAwait(false);
            var tags = await http.GetFromJsonAsync<TagsDto>("/api/tags", Json, timeout.Token).ConfigureAwait(false);
            var ps = await http.GetFromJsonAsync<PsDto>("/api/ps", Json, timeout.Token).ConfigureAwait(false);

            var models = (tags?.Models ?? [])
                .Where(m => !string.IsNullOrEmpty(m.Name))
                .Select(m => new LocalModel(m.Name!, m.Size, m.Digest, m.ModifiedAt, m.Details?.Family,
                    m.Details?.ParameterSize, m.Details?.QuantizationLevel))
                .ToList();
            var loaded = (ps?.Models ?? [])
                .Where(m => !string.IsNullOrEmpty(m.Name))
                .Select(m => new LoadedModel(m.Name!, m.Size, m.SizeVram, m.ExpiresAt, m.ContextLength))
                .ToList();

            return new HeartbeatMessage(version?.Version, true, models, loaded, activeStreams, HostStatsReader.Read());
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
                                       && !cancellationToken.IsCancellationRequested)
        {
            return new HeartbeatMessage(null, false, [], [], activeStreams, HostStatsReader.Read());
        }
    }

    private sealed record VersionDto(string? Version);

    private sealed record TagsDto(List<TagDto>? Models);

    private sealed record TagDto(string? Name, long Size, string? Digest,
        [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt, DetailsDto? Details);

    private sealed record DetailsDto(
        string? Family,
        [property: JsonPropertyName("parameter_size")] string? ParameterSize,
        [property: JsonPropertyName("quantization_level")] string? QuantizationLevel);

    private sealed record PsDto(List<PsModelDto>? Models);

    private sealed record PsModelDto(
        string? Name,
        long Size,
        [property: JsonPropertyName("size_vram")] long SizeVram,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("context_length")] int? ContextLength);
}

internal static class HostStatsReader
{
    public static HostStats Read()
    {
        double? load = null;
        long? total = null, available = null;
        try
        {
            if (File.Exists("/proc/loadavg"))
            {
                var parts = File.ReadAllText("/proc/loadavg").Split(' ');
                if (double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var l))
                {
                    load = l;
                }
            }

            if (File.Exists("/proc/meminfo"))
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    {
                        total = ParseKb(line);
                    }
                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    {
                        available = ParseKb(line);
                    }
                }
            }
        }
        catch (IOException)
        {
            // Host stats are optional.
        }

        return new HostStats(Environment.ProcessorCount, load, total, available);
    }

    private static long? ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb * 1024 : null;
    }
}
