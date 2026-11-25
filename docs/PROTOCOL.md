# Wire protocol (relay ⇄ node)

The node agent dials **out** to the relay and keeps one WebSocket open. Every inference call the relay
forwards to that node is a *stream* multiplexed over this one socket. Bodies are opaque bytes: the
protocol never interprets them.

Protocol version: **1** (`ProtocolConstants.Version` in `shared/OllamaRelay.Protocol`).

## Connection

1. The node opens `wss://<relay>/node/connect` with the header `Authorization: Bearer orn_…`.
   The relay answers `401` for an unknown or revoked token, before the WebSocket upgrade.
2. The node sends **Hello**. The relay checks the protocol version.
   - Mismatch: the relay closes with status `4001` and a description naming the version it speaks,
     and logs the refusal. The node logs it and retries slowly.
   - Match: the relay sends **HelloAck** with the node's name and the heartbeat interval.
3. The node sends a **Heartbeat** right away and then every `heartbeatIntervalSeconds`.
   The relay drops a node whose last heartbeat is older than `NODE_OFFLINE_AFTER_SECONDS` (default 20 s).
4. Any number of streams run concurrently until either side closes the socket.

A second connection with the same token replaces the first one (close status `4002`) and is written to the
audit log. Token rotation, revocation and node deletion close the connection with status `4003`.

The node reconnects after any drop with exponential backoff and full jitter (1 s to 60 s). A session that
stayed up for a minute resets the backoff.

## Framing

One frame per binary WebSocket message. Text messages are a protocol error.

```
offset  size  field
0       1     frame type
1       4     stream id, unsigned 32 bit, big endian (0 for connection level frames)
5       n     payload
```

The largest accepted message is 4 MiB. Senders split bodies into chunks of at most 64 KiB.
JSON payloads use camelCase property names.

| Type | Hex  | Direction    | Stream | Payload |
|------|------|--------------|--------|---------|
| Hello        | `0x01` | node → relay | 0 | `{"protocolVersion":1,"agentVersion":"0.1.0"}` |
| HelloAck     | `0x02` | relay → node | 0 | `{"protocolVersion":1,"nodeName":"diimitra","heartbeatIntervalSeconds":5}` |
| Heartbeat    | `0x03` | node → relay | 0 | see below |
| RequestHead  | `0x10` | relay → node | n | `{"method":"POST","path":"/api/chat","contentType":"application/json"}` |
| RequestBody  | `0x11` | relay → node | n | raw bytes |
| RequestEnd   | `0x12` | relay → node | n | empty |
| ResponseHead | `0x20` | node → relay | n | `{"status":200,"contentType":"application/x-ndjson"}` |
| ResponseBody | `0x21` | node → relay | n | raw bytes |
| ResponseEnd  | `0x22` | node → relay | n | empty |
| Cancel       | `0x30` | both         | n | `{"reason":"client_gone"}` |
| Error        | `0x31` | node → relay | n | `{"code":"upstream_unreachable"}` |

Cancel reasons and error codes are fixed identifiers. They never carry free text, so no upstream message
(which might quote a prompt) can travel inside them.

- Cancel reasons: `client_gone`, `revoked`, `overflow`, `timeout`, `shutdown`.
- Error codes: `not_allowed`, `upstream_unreachable`, `upstream_failed`, `bad_request`, `internal`.

### Heartbeat

```json
{
  "ollamaVersion": "0.13.1",
  "ollamaReachable": true,
  "models": [
    { "name": "qwen3:30b-a3b", "size": 18556688736, "digest": "…", "modifiedAt": "…",
      "family": "qwen3moe", "parameterSize": "30.5B", "quantizationLevel": "Q4_K_M" }
  ],
  "loaded": [
    { "name": "qwen3:30b-a3b", "size": 20000000000, "sizeVram": 20000000000,
      "expiresAt": "…", "contextLength": 8192 }
  ],
  "activeStreams": 1,
  "host": { "cpuCount": 32, "loadAverage1": 1.2, "memoryTotalBytes": 0, "memoryAvailableBytes": 0 }
}
```

`models` comes from Ollama's `GET /api/tags`, `loaded` from `GET /api/ps`. If Ollama does not answer,
`ollamaReachable` is false and both lists are empty. `host` is optional.

## Streams

Stream ids are chosen by the relay, unique per connection, and never reused on that connection.

```
relay                                   node
  | RequestHead (method, path)  ------->  |  allowlist check
  | RequestBody … RequestBody  -------->  |  buffered in memory
  | RequestEnd  ----------------------->  |  HTTP call to OLLAMA_URL + path
  |  <-------------------  ResponseHead   |  status and content type from Ollama
  |  <-------------------  ResponseBody   |  as Ollama produces bytes
  |  <-------------------  ResponseEnd    |
```

- If the method and path are not on the node's allowlist, the node answers **Error** `not_allowed` right
  after RequestHead and ignores the rest of that stream.
- If Ollama cannot be reached, the node answers **Error** `upstream_unreachable` instead of ResponseHead.
- If the upstream connection breaks after ResponseHead, the node sends **Error** `upstream_failed`.
- Upstream error bodies (status ≥ 400) are passed through as ResponseBody like any other body. Neither side
  logs them.
- **Cancel** from the relay makes the node abort its HTTP call to Ollama at once, so the GPU stops working
  for a client that left. The node sends nothing more on that stream.
- When the socket drops, both sides fail all open streams. The relay ends affected client responses with an
  error; there is no retry on another node.

### Node allowlist

The node only ever calls its fixed `OLLAMA_URL`, and only these exact method and path pairs:

```
POST /api/chat            POST /v1/chat/completions
POST /api/generate        POST /v1/completions
POST /api/embed           POST /v1/embeddings
POST /api/embeddings      POST /v1/responses
POST /api/show
```

Query strings, fragments, percent encoding, dot segments, double slashes and absolute URLs are refused.
This list duplicates the relay's on purpose: a compromised relay cannot reach `pull`, `delete`, `create`,
`push`, `copy` or any other host.

## Flow control

- Frames from all streams share the socket. The relay reads them in one loop and hands each frame to its
  stream's buffer, so a slow client never blocks the socket.
- Each stream's buffer on the relay holds at most `STREAM_BUFFER_KB` (default 8 MiB). If a client reads
  slower than the node produces and the buffer overflows, the relay cancels **that stream only**
  (reason `overflow`); every other stream on the socket continues.
- Sends on either side go through one writer queue per socket with back-pressure.
- Request bodies are bounded by the relay's `MAX_REQUEST_BODY_MB` before they are sent, and the node refuses
  bodies above 64 MiB.
