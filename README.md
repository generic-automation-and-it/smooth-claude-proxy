# SmoothClaudeProxy

A .NET YARP reverse proxy that sits between Claude Code and the Anthropic API. Captures user auth details into a local LiteDB database while transparently forwarding all requests. Supports session override mode to switch between accounts by label, and model routing to a local LM Studio instance.

## Login

Login can be done directly through the proxy. Set the env var first, then log in — the proxy will capture the new token automatically.

```bash
# 1. Point Claude Code at the proxy
export ANTHROPIC_BASE_URL=http://localhost:5066

# 2. Log in (opens browser for OAuth) — the proxy captures the new token
claude login

# 3. Start using Claude — traffic flows through the proxy
claude
```

Each unique bearer token is auto-registered in the proxy database with a random label. Use `GET /logins` to see tracked tokens, `PATCH /logins/{token}/label` to rename, and `POST /override/{label}` to switch between accounts.

To add another account, repeat the same steps in any terminal that has `ANTHROPIC_BASE_URL` set — each new login is captured and registered automatically.

## Architecture

```
Claude Code  -->  localhost:5066 (YARP in Docker)  -->  https://api.anthropic.com
                         |
                   LiteDB + Serilog
                   ~/.claude/proxy/
```

Model routing (optional):

```
Claude Code  -->  localhost:5066  -->  http://localhost:1234 (LM Studio)
                  (Haiku model pattern matched → rewritten + forwarded)
```

## Prerequisites

- Docker Compose **or** Podman
- Claude Code CLI (`claude login` completed)

## Quick Start

### Docker Compose

```bash
# Build and run
docker compose up --build -d

# View logs
docker compose logs -f

# Stop
docker compose down
```

### Podman Compose

```bash
# Build and run
podman-compose up --build -d

# View logs
podman-compose logs -f

# Stop
podman-compose down
```

**Full rebuild** (use this when code changes aren't being picked up):

```bash
podman-compose down \
  && podman rmi macau-v1_claude-proxy --force 2>/dev/null; \
  podman-compose build --no-cache \
  && podman-compose up -d
```

> `--no-cache` alone isn't enough — `podman-compose up` will reuse the existing tagged image even after a rebuild. Explicitly removing the image forces Podman to use the freshly built one.

Check which image name Podman is using if the above fails:

```bash
podman images | grep claude-proxy
```

### Podman

```bash
# Build the image
podman build -t smooth-claude-proxy .

# Run with host directory mounted
podman run -d \
  --name smooth-claude-proxy \
  -p 5066:5066 \
  -v ~/.claude/proxy:/data:Z \
  -e WORKSPACE_PATH=/data \
  -e LOG_TOKEN_FORMAT=true \
  --restart unless-stopped \
  smooth-claude-proxy
```

## Using with Claude Code

```bash
export ANTHROPIC_BASE_URL=http://localhost:5066
claude
```

Login can be done through the proxy — set `ANTHROPIC_BASE_URL` first, then run `claude login` to capture the new token automatically.

## Workspace

All data is stored at `~/.claude/proxy/` on the host (override with `CLAUDE_PROXY_DIR`):

```
~/.claude/proxy/
├── claude-auth.db              # LiteDB database (keyed by bearer token)
└── logs/
    └── claude-proxy-YYYYMMDD.log
```

## API Endpoints

Interactive docs available at **http://localhost:5066/scalar/v1** after startup.

### Proxy

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Health check, returns proxy target |

```bash
curl http://localhost:5066/health
```

### Logins

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/logins` | List all tracked tokens with masked token, label, rate limits |
| `PATCH` | `/logins/{bearerToken}/label` | Assign a friendly label to a token |

```bash
curl http://localhost:5066/logins
curl -X PATCH http://localhost:5066/logins/{token}/label \
  -H "Content-Type: application/json" \
  -d '"my-label"'
```

### Override Session

Override mode: when an active session is set, **all** proxied Anthropic requests use that session's credentials instead of the inbound token. Inbound tokens are not recorded to the DB while override is active.

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/override/{identifier}` | Activate a session by email or label |
| `GET` | `/override` | Get current active session (token masked) |
| `DELETE` | `/override` | Clear active session, resume pass-through mode |

```bash
# Activate by label — all subsequent Claude Code requests use their token
curl -X POST http://localhost:5066/override/my-label

# Check who is active
curl http://localhost:5066/override

# Return to normal pass-through mode
curl -X DELETE http://localhost:5066/override
```

### Model Routing (LM Studio)

Routes requests matching a model pattern to a local LM Studio instance instead of Anthropic. Defaults to routing `Haiku` → `qwen/qwen2.5-coder-14b`.

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/override-model` | Get current routing settings |
| `POST` | `/override-model` | Update routing settings |
| `DELETE` | `/override-model` | Reset to defaults |

```bash
# Check current routing
curl http://localhost:5066/override-model

# Enable routing with custom models
curl -X POST http://localhost:5066/override-model \
  -H "Content-Type: application/json" \
  -d '{"enabled":true,"fromModel":"Haiku","toModel":"qwen/qwen2.5-coder-14b"}'

# Disable routing
curl -X POST http://localhost:5066/override-model \
  -H "Content-Type: application/json" \
  -d '{"enabled":false}'
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `CLAUDE_PROXY_DIR` | `~/.claude/proxy` | Host path for DB and logs (compose only) |
| `WORKSPACE_PATH` | `/data` | Container-internal workspace path |
| `LOG_TOKEN_FORMAT` | `true` | Log bearer token format for debugging |
| `LMSTUDIO_BASE_URL` | `http://localhost:1234` | LM Studio base URL for model routing |

## How It Works

1. Claude Code sends requests with `Authorization: Bearer <token>`
2. If an **active session** is cached, auth headers are replaced before forwarding — inbound token is ignored
3. If the request `model` matches the routing pattern (default: "Haiku"), it is forwarded to LM Studio instead of Anthropic, with Anthropic→OpenAI format conversion
4. Otherwise, the token is recorded to LiteDB via a background channel (non-blocking)
5. YARP forwards the request to `api.anthropic.com`; SSE streaming passes through unbuffered
6. Rate limit headers from the response are captured and persisted per token
