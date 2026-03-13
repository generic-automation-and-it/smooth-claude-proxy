# Claude Code YARP Proxy

A .NET YARP reverse proxy that sits between Claude Code and the Anthropic API. Captures user auth details (email, name, API key, anthropic-version) into a local LiteDB database while transparently forwarding all requests.

## Architecture

```
Claude Code  -->  localhost:5000 (YARP in Docker)  -->  https://api.anthropic.com
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
  -p 5000:5000 \
  -v ~/.claude/proxy:/data:Z \
  -e WORKSPACE_PATH=/data \
  -e LOG_TOKEN_FORMAT=true \
  --restart unless-stopped \
  claude-proxy

# View logs
podman logs -f claude-proxy

# Stop
podman stop claude-proxy && podman rm claude-proxy
```

### Podman with podman-compose

```bash
podman-compose up --build -d
podman-compose logs -f
podman-compose down
```

## Using with Claude Code

```bash
ANTHROPIC_BASE_URL=http://localhost:5000 claude
```

Login must be done directly first (one-time browser OAuth flow):

```bash
claude login
```

## Workspace

All data is stored at `~/.claude/proxy/` on the host (override with `CLAUDE_PROXY_DIR`):

```
~/.claude/proxy/
├── claude-auth.db              # LiteDB database
└── logs/
    └── claude-proxy-YYYYMMDD.log  # Daily rolling log files
```

## API Endpoints

| Endpoint | Description |
|----------|-------------|
| `GET /health` | Health check, shows proxy target |
| `GET /users` | List all tracked users from LiteDB |

```bash
# Health check
curl http://localhost:5000/health

# View tracked users
curl http://localhost:5000/users
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `CLAUDE_PROXY_DIR` | `~/.claude/proxy` | Host path for DB and logs (compose only) |
| `WORKSPACE_PATH` | `/data` | Container-internal workspace path |
| `LOG_TOKEN_FORMAT` | `true` | Log Bearer token format for debugging |

## How It Works

1. Claude Code sends requests with `Authorization: Bearer <token>` header
2. YARP forwards all headers and body to `api.anthropic.com`
3. The middleware extracts user info from the JWT (if the token is a JWT)
4. A background worker upserts the record into LiteDB (non-blocking)
5. SSE streaming responses pass through unbuffered
