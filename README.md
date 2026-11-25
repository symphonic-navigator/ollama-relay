# ollama-relay

Share the Ollama in your homelab with a few friends, without opening a port at home and without a VPN.

`ollama-relay` is a small relay on a public VPS. Inference nodes next to Ollama dial **out** to the relay
over a WebSocket. Friends talk to the relay with any Ollama or OpenAI client and an API key. You decide who
may use which model on which node, and you see load and usage.

```
 clients (Open WebUI, Hermes Agent, curl …)
        |  HTTPS, Authorization: Bearer orl_…
        v
 +------------------------------+        +---------+
 | relay  (VPS, behind Traefik) |------->| MongoDB |
 |  - web UI + management API   |        +---------+
 |  - inference API, 2 dialects |
 |  - node hub (WebSocket)      |
 +------------------------------+
        ^  WSS, dialed by the node, Authorization: Bearer orn_…
        |
 +------------------------------+
 | node agent (homelab)         |----> Ollama on 127.0.0.1:11434
 +------------------------------+
```

This is a homelab tool for a handful of people, not a product. It aims to be simple wherever that does not
cost security.

## What it does

- **Nodes dial out.** No inbound port, no VPN. A node reconnects on its own with backoff.
- **Default deny.** A node reports its local models; nothing is usable until the admin *publishes* it under
  a short alias (`hf.co/someone/Long-Name-GGUF:Q4_K_M` becomes `lit-writer`) and *grants* it to users.
  The same alias on several nodes forms a pool the relay routes across.
- **Both dialects.** Ollama native (`/api/*`) and OpenAI compatible (`/v1/*`), streaming included.
  Clients only ever see aliases.
- **Invite only.** The admin creates invite links; users pick their own username and password and create
  as many API keys as they like.
- **Load and usage.** Live view of nodes, loaded models and running streams; usage per user, model and node
  over time. Metadata only.
- **No content, structurally.** Prompts and completions exist only in memory while a call runs. They are
  never logged, stored or put in an error report. A test pushes a canary string through every path and
  checks every log line and every database document for it.
- **Two allowlists.** The relay exposes only inference endpoints, and the node agent has its own allowlist.
  Even a compromised relay cannot make a node pull, delete or create models, or talk to another host.

Not included, on purpose: a chat UI (use Open WebUI or whatever you like), quotas or billing, model
management through the relay, public signup, e-mail, high availability.

## Repository layout

```
relay/backend/    ASP.NET Core relay (C#, .NET 10)
relay/frontend/   React + Vite + Tailwind SPA, served by the relay
relay/Dockerfile  one image: backend plus built frontend
node/             headless node agent (.NET worker) + Dockerfile
shared/           wire protocol library used by relay and node
tests/            unit tests and integration tests (relay + node + fake Ollama + MongoDB)
deploy/           compose files and .env examples for relay and node
docs/             PROTOCOL.md (relay ⇄ node wire protocol), project-brief.md (original design brief)
```

## Deploying the relay (VPS, Docker Compose, Traefik)

The relay runs as one container plus MongoDB. TLS terminates at Traefik. `deploy/relay/compose.yaml` works
as a plain compose project and as a [Komodo](https://komo.do) stack.

1. Copy `deploy/relay/compose.yaml` and `deploy/relay/.env.example` to the VPS, rename the example to `.env`
   (in Komodo: paste it into the stack's environment) and fill it in:

   | Variable | Meaning |
   |---|---|
   | `RELAY_HOST` | Public host name, used in the Traefik rule. |
   | `TRAEFIK_NETWORK`, `TRAEFIK_ENTRYPOINT`, `TRAEFIK_CERTRESOLVER` | Your Traefik setup. Defaults: `traefik`, `websecure`, `letsencrypt`. |
   | `MONGO_ROOT_USERNAME`, `MONGO_ROOT_PASSWORD` | MongoDB root account, created on first start. |
   | `MONGO_URL` | Connection string for the relay, with the same credentials. |
   | `PUBLIC_BASE_URL` | `https://relay.example.net`. Used in invite links and the "how to connect" box. |
   | `JWT_SIGNING_KEY` | At least 32 characters: `openssl rand -base64 48`. |
   | `ADMIN_USERNAME`, `ADMIN_INITIAL_PASSWORD` | First admin, created only if no admin exists. A password change is forced at first login. |
   | `TRUST_FORWARDED_HEADERS` | `true` behind Traefik, so failed logins are logged with the real client IP. |

   Optional tuning variables are listed at the end of `.env.example`.
2. `docker compose up -d`, open `https://relay.example.net`, sign in as the admin and set your own password.

MongoDB is only on an internal compose network with authentication on. The relay container runs as non-root
with a read-only root filesystem and no capabilities.

### Requirements on the proxy path

Whatever sits in front of the relay (Traefik in the example) must:

- **Pass WebSocket upgrades** on `/node/connect`. Traefik does this out of the box.
- **Not buffer responses.** Tokens must reach clients as they are generated. Traefik streams by default;
  do not add a buffering middleware.
- **Allow long requests.** A long generation can run for many minutes. Traefik v3 has no write timeout by
  default; if you set `respondingTimeouts` on the entrypoint, keep `writeTimeout` at `0` or well above your
  longest generation. The relay's own limits are `STREAM_IDLE_TIMEOUT_SECONDS` (default 10 min without a
  byte) and `STREAM_TOTAL_TIMEOUT_SECONDS` (default 2 h).
- **Not log bodies.** Traefik's access log never contains bodies; do not add plugins or middlewares that
  capture them. Invite and reset tokens travel in the URL fragment, so they never show up in access logs.
- **Allow large request bodies.** Multimodal requests carry images; the relay accepts up to
  `MAX_REQUEST_BODY_MB` (default 32).

### Locked out?

If the last admin forgot their password, reset it from a shell on the VPS. This needs shell access, which
is the intended bar:

```sh
docker compose exec relay dotnet OllamaRelay.Relay.dll reset-admin-password chris
```

It prints a generated password (or reads one from stdin), re-enables the account, ends its sessions and
forces a password change at the next login. The action is written to the audit log.

## Running a node (homelab, Docker or Podman)

1. In the relay UI go to **Nodes → Add a node**. The relay shows the node token **once**.
2. On the machine with Ollama, put `deploy/node/compose.yaml` and a `.env` next to each other:

   ```sh
   RELAY_URL=wss://relay.example.net/node/connect
   NODE_TOKEN=orn_…
   OLLAMA_URL=http://127.0.0.1:11434
   ```

3. Start it with `docker compose up -d`, or with Podman:

   ```sh
   podman run -d --name ollama-relay-node --restart=always \
     --network=host --env-file .env ghcr.io/symphonic-navigator/ollama-relay-node:latest
   ```

The node shows up as online within a few seconds and reports its models. Go to **Publish** to give them
aliases and to **Grants** to hand them out.

**Why host networking:** Ollama listens on `127.0.0.1:11434` by default, which a container on a bridge
network cannot reach. Host networking reaches it without changing Ollama. The agent opens no port of its own.

**Without host networking** you can point `OLLAMA_URL` at the host, for example
`http://host.docker.internal:11434` (Docker, add `extra_hosts: ["host.docker.internal:host-gateway"]`) or
`http://host.containers.internal:11434` (Podman). That requires Ollama to listen beyond loopback
(`OLLAMA_HOST=0.0.0.0`), which exposes Ollama's full API, including model management, to your LAN.
That is why host networking is the recommended setup.

`RELAY_URL` must be `wss://`. Plain `ws://` is only accepted with `RELAY_ALLOW_INSECURE=true`, for local
development.

## Connecting clients

Every user creates API keys under **API keys** (shown once, format `orl_…`).

| Dialect | Base URL | Endpoints |
|---|---|---|
| Ollama | `https://relay.example.net` | `POST /api/chat`, `/api/generate`, `/api/embed`, `/api/embeddings`, `/api/show`; `GET /api/tags`, `/api/ps`, `/api/version` |
| OpenAI | `https://relay.example.net/v1` | `POST /v1/chat/completions`, `/v1/completions`, `/v1/embeddings`, `/v1/responses`; `GET /v1/models`, `/v1/models/{model}` |

```sh
curl https://relay.example.net/api/chat \
  -H "Authorization: Bearer $OLLAMA_RELAY_KEY" \
  -d '{"model":"lit-writer","messages":[{"role":"user","content":"Hello!"}]}'
```

**Open WebUI:** add an Ollama connection with the base URL and the key as bearer token, or an OpenAI
connection with the `/v1` URL and the key.

Behaviour worth knowing:

- Model lists (`/api/tags`, `/v1/models`, `/api/ps`) are built by the relay and only contain the aliases
  granted to the caller. `alias` and `alias:latest` are the same model.
- An unknown model and a model you were not granted give the same 404.
- `keep_alive` is removed from requests (so nobody pins a model in memory forever); the node's Ollama
  default applies. Set `STRIP_KEEP_ALIVE=false` to pass it through.
- For OpenAI streaming the relay asks for a final usage chunk (`stream_options.include_usage`), so tokens
  can be counted. Clients receive that chunk too.
- When all nodes for a model are busy, requests wait up to `QUEUE_TIMEOUT_SECONDS` (default 30) and then get
  `503` with `Retry-After`. Each user can run `USER_MAX_CONCURRENT_STREAMS` (default 4) requests at once.
- When a client disconnects, the relay cancels the call on the node, which aborts the call to Ollama.
- Revoking a key or disabling a user stops their running requests immediately.

## Privacy and security notes

- **The relay sees content in transit.** Prompts and completions pass through the relay's memory in clear
  text, as with any HTTPS reverse proxy. Standard clients cannot do end-to-end encryption to the node.
  The relay never writes content to disk, logs, the database or error reports.
- **Leave `OLLAMA_DEBUG` off on nodes.** With it on, Ollama logs prompts itself.
- Stored data: accounts, API key and token *hashes*, publications, grants, usage metadata (user, key,
  node, model, endpoint, timings, token counts, outcome) and an audit log. Raw usage records expire after
  30 days; daily totals are kept. IP addresses are not stored; failed logins go to stdout with the source IP.
- Passwords use argon2id (64 MiB, 3 iterations). API keys, node tokens, invite, reset and refresh tokens
  carry 256 random bits and are stored as SHA-256 hashes.
- Web sessions: 15 minute access tokens held in memory by the web app, plus a rotating refresh token in an
  `HttpOnly`, `Secure`, `SameSite=Strict` cookie scoped to `/mgmt/auth`. Reusing a rotated refresh token ends
  the whole session. Password change, disable and reset end all sessions.
- API keys work only on `/api` and `/v1`, never on the management API.
- Logins, invite redemption and failed API key attempts are rate limited per IP.
- Forgotten passwords: the admin creates a reset link in **Users** (single use, 72 h). API keys stay valid.
- Deleting a user removes the account, keys, grants and sessions. Usage history stays and is shown as
  "deleted user (name)".

## Development

Requirements: .NET 10 SDK, Node.js 22 with pnpm, Docker (for the integration tests).

```sh
# Backend and node
dotnet build OllamaRelay.slnx
dotnet test tests/OllamaRelay.UnitTests
dotnet test tests/OllamaRelay.IntegrationTests   # starts MongoDB via Testcontainers

# Relay against a local MongoDB
docker run -d --name orl-mongo -p 27017:27017 mongo:8
JWT_SIGNING_KEY=$(openssl rand -base64 48) ADMIN_USERNAME=admin ADMIN_INITIAL_PASSWORD=change-me-please \
  COOKIE_SECURE=false dotnet run --project relay/backend/OllamaRelay.Relay   # http://localhost:8080

# Web UI with hot reload, proxying /mgmt to the relay
cd relay/frontend && pnpm install && pnpm dev   # http://localhost:5173

# A node against the local relay and a local Ollama
RELAY_URL=ws://localhost:8080 RELAY_ALLOW_INSECURE=true NODE_TOKEN=orn_… \
  dotnet run --project node/OllamaRelay.Node
```

The integration tests run the real relay, the real node agent and a fake Ollama over real sockets with a
real MongoDB. They cover streaming in both dialects, alias rewriting both ways, both allowlists, cancel
propagation, queue timeout, node reconnect, immediate revocation and the no-content canary.

CI (`.github/workflows/ci.yml`) builds and tests every push and pull request, and publishes multi-arch
images (`linux/amd64`, `linux/arm64`) to `ghcr.io/symphonic-navigator/ollama-relay` and
`ghcr.io/symphonic-navigator/ollama-relay-node` from the default branch and from `v*` tags. Both builds also move `:latest`, so `latest` is always the newest build.

The relay ⇄ node wire protocol is specified in [docs/PROTOCOL.md](docs/PROTOCOL.md).

## License

[GNU Affero General Public License v3.0 or later](LICENSE). A Tidesson Communications project.
