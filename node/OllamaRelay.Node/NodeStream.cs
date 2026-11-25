using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using OllamaRelay.Protocol;

namespace OllamaRelay.Node;

/// <summary>
/// One multiplexed request. Buffers the request body in memory, calls the local Ollama and
/// streams the response back. Bodies are never logged.
/// </summary>
internal sealed class NodeStream : IDisposable
{
    private readonly uint _id;
    private readonly RequestHeadMessage _head;
    private readonly FrameSocket _socket;
    private readonly OllamaClient _ollama;
    private readonly ILogger _logger;
    private readonly int _maxBody;
    private readonly CancellationTokenSource _cts;
    private ArrayBufferWriter<byte>? _body = new();

    public NodeStream(uint id, RequestHeadMessage head, FrameSocket socket, OllamaClient ollama, ILogger logger,
        int maxBody, CancellationToken connectionToken)
    {
        _id = id;
        _head = head;
        _socket = socket;
        _ollama = ollama;
        _logger = logger;
        _maxBody = maxBody;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
    }

    public string? CancelReason { get; private set; }

    /// <summary>Returns false when the body exceeded the limit; the stream is then failed.</summary>
    public bool AppendBody(ReadOnlySpan<byte> chunk)
    {
        if (_body is null)
        {
            return false;
        }

        if (_body.WrittenCount + chunk.Length > _maxBody)
        {
            ClearBody();
            return false;
        }

        _body.Write(chunk);
        return true;
    }

    public void Cancel(string reason)
    {
        CancelReason ??= reason;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    public async Task RunAsync()
    {
        var started = Stopwatch.GetTimestamp();
        var status = 0;
        var outcome = "ok";
        var token = _cts.Token;
        try
        {
            var body = _body?.WrittenMemory.ToArray() ?? [];
            ClearBody();

            using var request = new HttpRequestMessage(new HttpMethod(_head.Method), _head.Path);
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = MediaTypeHeaderValue.TryParse(_head.ContentType, out var ct)
                ? ct
                : new MediaTypeHeaderValue("application/json");
            request.Content = content;

            HttpResponseMessage response;
            try
            {
                response = await _ollama.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                outcome = "upstream_unreachable";
                await _socket.SendAsync(ProtocolJson.ToFrame(FrameType.Error, _id,
                    new ErrorMessage(ErrorCodes.UpstreamUnreachable)), CancellationToken.None).ConfigureAwait(false);
                return;
            }
            finally
            {
                Array.Clear(body);
            }

            using (response)
            {
                status = (int)response.StatusCode;
                await _socket.SendAsync(ProtocolJson.ToFrame(FrameType.ResponseHead, _id,
                    new ResponseHeadMessage(status, response.Content.Headers.ContentType?.ToString())), token)
                    .ConfigureAwait(false);

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var buffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.MaxBodyChunk);
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(0, ProtocolConstants.MaxBodyChunk), token)
                               .ConfigureAwait(false)) > 0)
                    {
                        await _socket.SendAsync(new Frame(FrameType.ResponseBody, _id, buffer.AsSpan(0, read).ToArray()),
                            token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }

                await _socket.SendAsync(Frame.Empty(FrameType.ResponseEnd, _id), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = "cancelled:" + (CancelReason ?? CancelReasons.Shutdown);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            outcome = "upstream_failed";
            _socket.TrySend(ProtocolJson.ToFrame(FrameType.Error, _id, new ErrorMessage(ErrorCodes.UpstreamFailed)));
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            outcome = "relay_gone";
        }
        finally
        {
            // Only metadata: stream id, endpoint, status and timing. Never body content.
            _logger.LogInformation("stream {StreamId} {Method} {Path} -> {Status} {Outcome} in {ElapsedMs} ms",
                _id, _head.Method, _head.Path, status, outcome,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private void ClearBody()
    {
        if (_body is null)
        {
            return;
        }

        _body.Clear();
        _body = null;
    }

    public void Dispose()
    {
        ClearBody();
        _cts.Dispose();
    }
}
