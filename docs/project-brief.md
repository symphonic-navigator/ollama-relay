# ollama-relay — Project Brief

**Date:** 2026-11-25
**Status:** Draft for approval
**Kind:** Ad hoc homelab project, greenfield, monorepo

Lines marked **(call)** are decisions made while writing this brief, not things Chris explicitly decided. They are sensible defaults and may be overruled.

---

## 1. Intent

Chris runs Ollama on a Strix Halo box (`diimitra`) in his homelab, used for experiments and occasional entertainment use cases. He wants to share it with a handful of friends without opening a port at home and without a VPN.

`ollama-relay` is a small relay on his public VPS. Inference nodes dial **out** to the relay over WebSockets; consumers talk to the relay with ordinary Ollama or OpenAI clients and an API key. The admin decides who may use which model on which node, and can see load and usage.

Expected scale: about 3 users (all friends), 1 node today, a few nodes later. This is not a product. Prefer the simple solution wherever the security properties below are not affected.

## 2. Goals

- Multiple inference nodes, multiple consumers, one relay.
- Only the admin can add nodes.
- Admin invites users; users set their own username and password.
- Users create any number of API keys and use them as bearer tokens from their clients.
- Admin publishes models under short aliases and grants them per user.
- Admin sees node load (which models are loaded, who is running what) and usage per user over time.
- Both API dialects: Ollama native and OpenAI compatible.
- **No prompt or completion content is ever logged or stored, anywhere.**
- Safe to expose on the public internet.

## 3. Non-goals

- No chat UI. Users install Open WebUI, Hermes Agent or whatever they like.
- No quotas, billing or hard usage limits in v1. Visibility only.
- No model management through the relay (`pull`, `delete`, `create`, `push`, `copy`). The admin manages models directly on the node.
- No node self-registration, no public signup, no e-mail sending (invite links are copied by hand).
- No Redis, no multi-instance relay, no high availability.
- No end-to-end encryption between client and node. Standard clients cannot do it.
- No field-level database encryption (see section 9.6).
- No GUI on the node.

## 4. Anti-goals

The project has failed, even if it works, if any of these is true:

- Prompt or completion content ends up in a log line, a database document, a temp file, a trace or an error report.
- A node needs an inbound port or a VPN.
- A user can list or address a model that was not published and granted to them.
- A compromised relay can make a node do anything other than allowlisted inference calls.

## 5. Approaches considered

| | Approach | Trade-off |
|---|---|---|
| **A** | Custom multiplexed protocol over plain WebSockets, small node agent | Bodies stay opaque bytes; node has its own allowlist; no inbound port. Framing, cancel and reconnect must be specified and tested by us. |
| B | SignalR between relay and node | Streaming and reconnect for free in C#. Payloads go through hub serialization and the design is tied to SignalR semantics for what is only a pipe. |
| C | No node agent: WireGuard to the homelab, relay as HTTP reverse proxy (YARP) | Least code by far. A compromised relay gets the full Ollama API on the node, and every node needs VPN setup. |

**Chosen: A.** Chris wants the control that B and C give away: opaque pass-through and a second allowlist on the node side.

## 6. Architecture

```
 clients (Open WebUI, Hermes Agent, curl ...)
        |  HTTPS, Authorization: Bearer <api key>
        v
 +-----------------------------+        +---------+
 | relay  (VPS, behind Traefik)|------->| MongoDB |
 |  - management API + web UI  |        +---------+
 |  - inference API (2 dialects)|
 |  - node hub (WebSocket)     |
 +-----------------------------+
        ^  WSS, dialed by the node, Authorization: Bearer <node token>
        |
 +-----------------------------+
 | node agent (homelab)        |----> Ollama on localhost:11434
 +-----------------------------+
```

### 6.1 Components

- **relay/backend** — ASP.NET Core. Identity, grants, routing, metering, node hub. The only component with a database.
- **relay/frontend** — React SPA with an admin area and a user area. Served as static files by the relay backend, so the relay is one container **(call)**.
- **node** — .NET worker service, headless. Configured through environment variables only. No database, no UI, no listening port.
- **shared** — C# library with the wire protocol types, used by relay and node.

### 6.2 URL layout on the relay

Ollama's native API lives under `/api`, so the management API must not.

| Path | Purpose | Auth |
|---|---|---|
| `/` | SPA | — |
| `/mgmt/*` | Management API for the SPA | JWT |
| `/api/*` | Ollama native dialect | API key |
| `/v1/*` | OpenAI compatible dialect | API key |
| `/node/connect` | WebSocket endpoint for nodes | node token |

## 7. Tech stack

- Backend and node: C#, .NET 10 (LTS), official MongoDB driver.
- Database: MongoDB only. Live state (connected nodes, active streams, queues) is held in memory.
- Frontend: TypeScript, React, Vite, Tailwind, pnpm.
- All UI text, code comments and documentation in English.
- License: GPLv3 **(call, Chris' usual choice)**.
- `README.md` is kept current with every change.

## 8. Functional design

### 8.1 Roles

- **Admin**: everything below, plus everything a user can do. One or more admins; the first one is bootstrapped (9.1).
- **User**: own account, own API keys, own usage, list of models granted to them.

### 8.2 Nodes

- Admin creates a node in the UI (name, max concurrency, default 2). The relay shows a node token **once**.
- Admin puts relay URL and token into the node's `.env` and starts the agent.
- Admin can rotate or revoke a token. Revoking closes the live connection immediately.
- A node is `online` while its WebSocket is up and heartbeats arrive.

### 8.3 Publications and aliases

- The node reports the models Ollama has locally. Nothing is usable until the admin **publishes** it.
- A publication is `(node, upstream model name, alias)`. Example: `hf.co/someone/Long-Name-GGUF:Q4_K_M` on `diimitra` published as `lit-writer`.
- Unpublished models do not exist from the outside. Default deny.
- The same alias on several nodes forms a pool the relay routes across.
- Alias format **(call)**: `^[a-z0-9][a-z0-9._-]{0,62}$`. The relay treats `alias` and `alias:latest` as the same name, because Ollama clients like to append `:latest`.
- Users work with aliases only: in requests, in model lists and in the `model` field of responses.
- Aliases exist for convenience, not for confidentiality **(call)**. The relay rewrites the `model` field and nothing else. If an upstream name shows through elsewhere (the body of `/api/show`, an Ollama error message), that is acceptable; do not build deep scrubbing for it.

### 8.4 Grants

- A grant is `(user, publication)`, which is the "user may use model X on node Y" triple Chris asked for.
- The admin UI shows this as a matrix or an equivalent view; granting an alias that exists on several nodes should be doable in one step.

### 8.5 Inference API

The relay is a thin pass-through. It touches the body in exactly two ways: rewriting model names and reading usage counters.

**Exposed endpoints** (anything else returns 404):

| Dialect | Endpoints |
|---|---|
| Ollama native | `POST /api/chat`, `POST /api/generate`, `POST /api/embed`, `POST /api/embeddings`, `GET /api/tags`, `POST /api/show`, `GET /api/ps`, `GET /api/version` |
| OpenAI compatible | `POST /v1/chat/completions`, `POST /v1/completions`, `POST /v1/embeddings`, `GET /v1/models`, `GET /v1/models/{model}` |

Verify the list against the current Ollama API docs when building; if Ollama offers further pure inference endpoints (for example `/v1/responses`), adding them is fine. Management endpoints stay out.

**Behaviour:**

- `GET /api/tags`, `GET /v1/models` and `GET /api/ps` are **synthesized by the relay** from publications, grants and the last node heartbeats. They are never forwarded. They list only what the calling user was granted, under alias names.
- For forwarded calls the relay resolves the alias, picks a node (8.6), rewrites `model` in the request to the upstream name and rewrites `model` in every response chunk back to the alias.
- Unknown alias and missing grant are indistinguishable: both return the dialect's "model not found" error (404).
- Streaming works for both dialects (NDJSON for native, SSE for OpenAI). First-byte latency must not be buffered away.
- `keep_alive` is stripped from user requests **(call)**, so a user cannot pin a model in memory forever. The node's Ollama default applies. One relay setting turns this off.
- For OpenAI streaming requests the relay sets `stream_options.include_usage = true` **(call)**, otherwise there are no token counts to meter. The extra usage chunk is passed on to the client.
- Tool calls, images and other payload features pass through untouched.
- Request body size limit: 32 MiB by default, configurable (multimodal requests carry images).

### 8.6 Routing and load

- Candidates: publications with the requested alias that the user was granted, on nodes that are online.
- Preference: a node that already has the model loaded (from the last heartbeat), then the node with the fewest active streams.
- Each node has a max concurrency. When all candidates are full, the request waits in a queue for up to 30 s (configurable), then gets 503 with `Retry-After`. The queue has a length cap.
- No candidate online: 503.
- Per-user limit on concurrent streams: one global setting, default 4 **(call)**. This is abuse protection, not a quota.
- If the client disconnects, the relay cancels the stream on the node and the node aborts the HTTP call to Ollama, so the GPU stops working for nobody. This must be covered by a test.
- If a node drops mid-stream, the affected client streams end with an error; no automatic retry on another node.

### 8.7 Node agent

- Configuration: `RELAY_URL` (must be `wss://`; plain `ws://` only with an explicit dev flag), `NODE_TOKEN`, `OLLAMA_URL` (default `http://127.0.0.1:11434`).
- Connects outbound, authenticates with the token, reconnects with exponential backoff and jitter.
- Sends a heartbeat every few seconds: Ollama version, local model list (`/api/tags`), loaded models with memory use and expiry (`/api/ps`), active stream count. Optional: basic host stats.
- **Own allowlist:** the agent forwards only the inference endpoints from 8.5 to its fixed `OLLAMA_URL`. The relay cannot name another host, and cannot trigger `pull`, `delete` and friends. This is deliberate duplication of the relay's allowlist.
- Knows nothing about users, aliases or grants.
- The allowlist is about endpoints, not models **(call)**. The node reports all local models, and the relay could technically run inference on an unpublished one. That is accepted: the same admin runs relay and nodes, and publishing from the reported list is the workflow. A per-node model allowlist is a possible later addition.

### 8.8 Wire protocol (relay ⇄ node)

- One WebSocket per node, binary frames, many concurrent streams multiplexed by stream id.
- Frame kinds needed: hello (protocol version, agent version), heartbeat, request head, request body chunk, request end, response head (status, content type), response body chunk, response end, cancel, error.
- Bodies are opaque bytes on the wire. The protocol never interprets them.
- Protocol version is negotiated in the hello; a mismatch is a clean, logged refusal.
- One slow consumer must not stall other streams on the same socket: per-stream bounded buffers; on overflow that one stream is cancelled.
- The exact frame layout is the builder's choice and must be documented in `docs/PROTOCOL.md`.

### 8.9 Usage metering

One record per inference call, **metadata only**:

- user, API key id, node, alias, upstream model, endpoint kind (chat / generate / embed)
- start time, queue wait, time to first byte, total duration
- prompt tokens, completion tokens (from the final chunk or usage block)
- outcome (ok / cancelled / error / timeout) and HTTP status

Retention **(call)**: raw records expire after 30 days (TTL index). Daily aggregates per user × alias × node are kept without limit.

Deleting a user **(call)**: account, API keys, grants and sessions are removed. Usage and audit documents carry the user id plus a username snapshot, so history stays readable and is shown as "deleted user (name)".

### 8.10 Web UI

**Admin area**

- Nodes: status, last heartbeat, versions, loaded models with memory use, active streams (who, which alias, since when), queue depth. Create, rotate token, revoke.
- Models: per node the reported local models, publish / rename alias / unpublish.
- Users: list, disable / enable, delete, invites (create, copy link, revoke), grants.
- Usage: requests, tokens and duration over time, filterable by user, alias and node.
- Audit log of admin actions.

**User area**

- Account: change password.
- API keys: create (shown once), name, prefix, created, last used, revoke.
- My models: granted aliases with online status.
- My usage.
- A short "how to connect" box with the two base URLs and a curl example.

Live views may simply poll every few seconds.

## 9. Security design

The relay is public. Threats considered: credential stuffing against the login, stolen API keys, a stolen node token (a rogue node would receive prompts in clear text), a compromised relay attacking nodes, and one user affecting another.

### 9.1 Accounts and sessions

- First admin is created on first start from `ADMIN_USERNAME` and `ADMIN_INITIAL_PASSWORD`, only if no admin exists. Password change is forced at first login.
- Invites: random token, single use, expires after 72 h, stored hashed. The invite page lets the user choose username and password.
- Passwords: argon2id with OWASP-recommended parameters or stronger, parameters stored with the hash. Minimum length 12, no composition rules.
- Forgotten password **(call)**: the admin issues a reset link from the user list. It follows the invite rules (random, single use, 72 h, stored hashed), revokes all sessions when used, and is written to the audit log. API keys stay valid. There is no self-service reset.
- Break-glass for a locked-out last admin **(call)**: a CLI command inside the relay container that sets a new admin password. It needs shell access to the VPS, which is the right bar. Documented in the README.
- Web sessions: short-lived JWT access token (15 min) held in memory by the SPA; rotating refresh token in an `HttpOnly`, `Secure`, `SameSite=Strict` cookie scoped to the auth path, stored hashed. Password change, disable and logout revoke refresh tokens.
- JWT signing key comes from the environment.
- Optional TOTP for admins is a nice-to-have, not required for v1.

### 9.2 API keys

- Format `orl_<prefix>_<secret>` with at least 256 bits of randomness; the prefix is shown in the UI to recognize a key.
- Stored as SHA-256 hash only. The keys are high entropy, and a slow hash per request would cost latency for nothing.
- Shown once at creation. Revocation and user disabling take effect immediately, including cancelling running streams.
- API keys work only on `/api/*` and `/v1/*`, never on `/mgmt/*`.

### 9.3 Node tokens

- Format `orn_...`, same storage rules as API keys. One token per node, never shared.
- A token authenticates exactly one node identity. A second connection with the same token replaces the first and is written to the audit log.

### 9.4 Hardening

- Endpoint allowlists on the relay and on the node (8.5, 8.7).
- Rate limiting on login, invite redemption and failed API key attempts (in memory is fine).
- Identical responses for "unknown user" and "wrong password", and for "unknown model" and "not granted".
- Body size limits, header limits, idle and total timeouts on streams.
- Security headers and a strict CSP for the SPA. No CORS for `/mgmt`; CORS on the inference API off by default, configurable.
- MongoDB is reachable only on the internal compose network, with authentication enabled.
- Containers run as non-root with a read-only root filesystem where practical.
- Audit log (database): admin actions, logins, key and token lifecycle. Security events such as failed logins go to stdout with source IP; IP addresses are not written to usage records.

### 9.5 No content, structurally

These are hard requirements, not guidelines:

- Request and response bodies exist only in memory, for the duration of the call. Never written to disk, database, logs, traces or metrics.
- ASP.NET HTTP logging middleware and any body-capturing diagnostics stay off. Exception handling never includes body content in messages.
- Upstream error bodies from Ollama are passed to the client and not logged (they can echo prompt fragments). Log status code and a generic reason only.
- Database schemas contain no free-text field that could carry content.
- A test sends a unique canary string as prompt through both dialects, including an error path and a cancelled stream, and then asserts the canary appears in neither relay logs, node logs nor any database collection.
- The README tells node operators to leave `OLLAMA_DEBUG` off, because Ollama then logs prompts itself, and states plainly that the relay sees content in memory in transit.

### 9.6 Data at rest

There is no content to protect, so there is no field-level encryption. All secrets (passwords, API keys, node tokens, invite tokens, refresh tokens) are stored as hashes only. What remains sensitive is usage metadata, handled by retention (8.9) and by not exposing MongoDB.

## 10. Data model (sketch)

Collections, names at the builder's discretion: `users`, `invites`, `refreshTokens`, `apiKeys`, `nodes`, `publications`, `grants`, `usageRecords` (TTL), `usageDaily`, `auditLog`.

## 11. Repository layout

```
ollama-relay/
  relay/
    backend/          ASP.NET Core
    frontend/         React, Vite, pnpm
    Dockerfile        multi-stage, builds frontend into the backend image
  node/
    Dockerfile
  shared/             wire protocol library
  deploy/
    relay/compose.yaml  + .env.example
    node/compose.yaml   + .env.example
  docs/PROTOCOL.md
  README.md
```

## 12. Deployment

Working examples for both targets are part of the deliverable, under `deploy/`, and are explained in the README.

### 12.1 Relay: Docker Compose, deployed through Komodo, behind Traefik

Chris' VPS runs Traefik; TLS terminates there. Sketch of what `deploy/relay/compose.yaml` should look like:

```yaml
services:
  relay:
    image: ghcr.io/OWNER/ollama-relay:latest
    restart: unless-stopped
    env_file: .env
    depends_on: [mongo]
    networks: [internal, traefik]
    labels:
      - traefik.enable=true
      - traefik.http.routers.ollama-relay.rule=Host(`relay.example.net`)
      - traefik.http.routers.ollama-relay.entrypoints=websecure
      - traefik.http.routers.ollama-relay.tls.certresolver=letsencrypt
      - traefik.http.services.ollama-relay.loadbalancer.server.port=8080

  mongo:
    image: mongo:8
    restart: unless-stopped
    env_file: .env
    volumes: [mongo-data:/data/db]
    networks: [internal]

networks:
  internal:
  traefik:
    external: true

volumes:
  mongo-data:
```

`.env.example` covers at least: public base URL, MongoDB credentials and connection string, JWT signing key, admin bootstrap credentials.

Requirements on the proxy path, to be stated in the README: WebSocket upgrade for `/node/connect`, no response buffering, timeouts long enough for long generations, and no body logging in Traefik.

### 12.2 Node: Docker or Podman

Ollama usually listens on `127.0.0.1:11434` on the host, so the simplest working setup is host networking:

```yaml
services:
  node:
    image: ghcr.io/OWNER/ollama-relay-node:latest
    restart: unless-stopped
    network_mode: host
    env_file: .env
```

```
podman run -d --name ollama-relay-node --restart=always \
  --network=host --env-file .env ghcr.io/OWNER/ollama-relay-node:latest
```

The README also shows the variant without host networking (`OLLAMA_URL` pointing at `host.docker.internal` / `host.containers.internal`, which requires Ollama to listen beyond loopback) and says why host networking is the recommended one.

How images get built and published (CI workflow or local build) is left to the builder; the compose files must work with either.

## 13. Testing

- Test-driven where practical. Unit tests for alias resolution, grants, routing and frame encoding.
- Integration tests run relay, node and a **fake Ollama** together, with a real MongoDB (Testcontainers): streaming in both dialects, alias rewrite in both directions, allowlists on both sides, cancel propagation, queue timeout, node reconnect, immediate revocation, and the canary test from 9.5.
- A short manual check against a real Ollama with Open WebUI and one OpenAI-style client before calling it done.

## 14. Left to the builder

- Exact frame layout and flow-control details (documented in `docs/PROTOCOL.md`).
- Management API shape, collection schemas and indexes.
- UI layout and component choices.
- What `GET /api/version` returns (a plausible Ollama version so clients are satisfied).
- Polling versus push for live views.
- Image build and publishing mechanics.

## 15. Open after v1

- Quotas and per-user limits.
- TOTP for admins.
- Per-publication settings such as a `keep_alive` policy or context size caps.
- A per-node model allowlist, so the relay only learns about models the node operator names.
