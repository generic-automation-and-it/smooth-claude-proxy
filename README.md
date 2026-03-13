# Claude Code YARP Proxy

A .NET YARP reverse proxy that sits between Claude Code and the Anthropic API. Captures user auth details into a local LiteDB database while transparently forwarding all requests. Supports session override mode to switch between accounts by label.

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

Each unique bearer token is auto-registered in the proxy database with a random label (e.g. "Mina Russel"). Use `GET /users` to see tracked tokens, `PATCH /users/{token}/label` to rename, and `POST /active/{label}` to switch between accounts.

To add another account, repeat the same steps in any terminal that has `ANTHROPIC_BASE_URL` set — each new login is captured and registered automatically.

## Architecture

```
Claude Code  -->  localhost:5066 (YARP in Docker)  -->  https://api.anthropic.com
                         |
                   LiteDB + Serilog
                   ~/.claude/proxy/
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

Rebuild from scratch (when cached layers hide code changes):

```bash
podman-compose down && podman-compose build --no-cache && podman-compose up -d
```

### Podman

```bash
# Build the image
podman build -t claude-proxy .

# Run with host directory mounted
podman run -d \
  --name claude-proxy \
  -p 5066:5066 \
  -v ~/.claude/proxy:/data:Z \
  -e WORKSPACE_PATH=/data \
  -e LOG_TOKEN_FORMAT=true \
  --restart unless-stopped \
  claude-proxy
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

### Users

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/users` | List all users tracked in LiteDB |

```bash
curl http://localhost:5066/users
```

### Active Session

Override mode: when an active session is set, **all** proxied Anthropic requests use that session's credentials instead of the inbound token. Inbound tokens are not recorded to the DB while override is active.

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/active/{identifier}` | Activate a session by email or label |
| `GET` | `/active` | Get current active session (token masked) |
| `DELETE` | `/active` | Clear active session, resume pass-through mode |

```bash
# Activate by label — all subsequent Claude Code requests use their token
curl -X POST http://localhost:5066/active/Mina%20Russel

# Check who is active
curl http://localhost:5066/active

# Return to normal pass-through mode
curl -X DELETE http://localhost:5066/active
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `CLAUDE_PROXY_DIR` | `~/.claude/proxy` | Host path for DB and logs (compose only) |
| `WORKSPACE_PATH` | `/data` | Container-internal workspace path |
| `LOG_TOKEN_FORMAT` | `true` | Log bearer token format for debugging |

## How It Works

1. Claude Code sends requests with `Authorization: Bearer <token>`
2. If an **active session** is cached, auth headers are replaced before forwarding — inbound token is ignored
3. Otherwise, the token is recorded to LiteDB via a background channel (non-blocking)
4. New tokens are auto-assigned a random label via Bogus and the active session is cleared so the new token becomes active
5. YARP forwards the request to `api.anthropic.com`; SSE streaming passes through unbuffered
6. Unified rate limit headers from the response are captured and persisted per token
