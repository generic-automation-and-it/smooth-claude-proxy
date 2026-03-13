# Claude Code YARP Proxy

A .NET YARP reverse proxy that sits between Claude Code and the Anthropic API. Captures user auth details into a local LiteDB database while transparently forwarding all requests. Supports session override mode to impersonate a specific user for all proxied traffic.

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

Login must be done directly first (one-time browser OAuth flow):

```bash
claude login
```

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
| `POST` | `/active/{email}` | Activate a user session by email |
| `GET` | `/active` | Get current active session (token masked) |
| `DELETE` | `/active` | Clear active session, resume pass-through mode |

```bash
# Activate a user — all subsequent Claude Code requests use their token
curl -X POST http://localhost:5066/active/user@example.com

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
4. For opaque OAuth tokens (`sk-ant-oat-*`), the worker calls `GET /v1/me` to resolve email/name
5. YARP forwards the request to `api.anthropic.com`; SSE streaming passes through unbuffered
