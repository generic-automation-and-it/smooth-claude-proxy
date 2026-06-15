# Conductor → OpenCode Go routing setup

## TL;DR

Running this script in a fresh Conductor sandbox **routes Claude Code through the
smooth-claude-proxy to OpenCode Go** (`opencode.ai/zen/go`) instead of
`api.anthropic.com`. It installs .NET + OpenCode, starts the proxy as a Docker
container pulled from GHCR, and points Claude Code at it via
`ANTHROPIC_BASE_URL=http://localhost:5066`. Each Claude model family is remapped
to an OpenCode model through env vars baked into the container:

| Claude family | Routed to (OpenCode `/v1/messages`) |
|:--------------|:------------------------------------|
| Fable         | `qwen3.7-max`  |
| Opus          | `qwen3.7-plus` |
| Sonnet        | `minimax-m3`   |
| Haiku         | `qwen3.6-plus` |

The proxy forwards the request body verbatim (Anthropic passthrough), injects the
OpenCode auth on every routed request, and streams the SSE response straight back.
The routed model identifies itself from Claude Code's verbatim system prompt, so
asking "what model are you?" may report the Claude family name — that's expected
and cosmetic, not a routing failure.

## Prerequisites / knobs

- **`LLMSERVICE_API_KEY`** — required (the provider-agnostic auth env var).
  **`export` it in your shell before running** — do **not** paste a real key into
  the script block below, because this file is tracked in the repo and a committed
  key would leak. The container starts without a key but every routed request 401s.
  The key is written only to `~/.claude/proxy/proxy.env` (mode 600, outside the repo)
  — never to a tracked file and never printed to the console (the verify step checks
  presence only).
- **`LLMSERVICE_BASEURL`** — optional. Overrides the upstream LLM URL
  (`LlmService:BaseUrl`, default `https://opencode.ai/zen/go`). Set it to point the
  proxy at a different Anthropic-compatible endpoint.
- **`GHCR_TOKEN` / `GHCR_USER`** — only needed if the GHCR package is private. With
  a public package the pull needs no auth.
- Model names use the **no-hyphen** form (`qwen3.7-plus`, not `qwen-3.7-plus`).
  Hyphenated names 401 on OpenCode.
- Proxy listens on **port 5066**; state + logs live in `~/.claude/proxy` (mounted
  at `/data` in the container).

## Why the helper + env-file shape

- A single helper, `~/.claude/proxy/proxy-up.sh`, is the **one source of truth** for
  "ensure dockerd + container are up". It starts `dockerd` only if it isn't already
  serving (never a second daemon — a duplicate daemon makes the container vanish with
  `No such container`) and `docker start`s the existing container rather than
  re-`run`ning it.
- Config (key + model overrides) lives in `~/.claude/proxy/proxy.env` (mode 600) and
  is passed via `--env-file`, so the `~/.bashrc` auto-start hook can re-run without
  secrets in shell config and without re-passing the key.

## Script

```bash
#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────
#  Fresh dev image setup: .NET (latest) + OpenCode (latest) installed
#  natively; the Claude proxy runs as a Docker container pulled from GHCR.
#  A single helper (~/.claude/proxy/proxy-up.sh) is the ONE source of
#  truth for "ensure dockerd + container are up". It is safe to run
#  repeatedly and is called both here and from the ~/.bashrc hook.
# ──────────────────────────────────────────────────────────────────────

PROXY_IMAGE="ghcr.io/generic-automation-and-it/smooth-claude-proxy:latest"
PROXY_DIR="$HOME/.claude/proxy"
ENV_FILE="$PROXY_DIR/proxy.env"
HELPER="$PROXY_DIR/proxy-up.sh"

# Prefer `export LLMSERVICE_API_KEY=...` in your shell before running this script.
# Do NOT commit a real key here — this file is tracked in the repo.
LLMSERVICE_API_KEY="${LLMSERVICE_API_KEY:-}"
# Optional: override the upstream LLM URL (defaults to opencode.ai/zen/go).
LLMSERVICE_BASEURL="${LLMSERVICE_BASEURL:-https://opencode.ai/zen/go}"

mkdir -p "$PROXY_DIR"

# ── 1) .NET SDK (latest 10.0) ──────────────────────────────────────────
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"

# ── 2) OpenCode (latest) — installer appends its own PATH line to ~/.bashrc
curl -fsSL https://opencode.ai/install | bash

# ── 2b) Additional CLIs: Claude Code, Codex, pi ─────────────────────────
curl -fsSL https://claude.ai/install.sh | bash
# bubblewrap is the Linux sandbox runtime Codex requires at runtime.
sudo dnf install -y bubblewrap
curl -fsSL https://chatgpt.com/codex/install.sh | CODEX_NON_INTERACTIVE=1 sh
curl -fsSL https://pi.dev/install.sh | sh

# ── 3) Docker engine ───────────────────────────────────────────────────
sudo dnf install -y docker

# ── 4) Write the container env file (single source of truth for config) ─
umask 077                                   # secrets: owner-read/write only
cat > "$ENV_FILE" <<EOF
LLMSERVICE_API_KEY=$LLMSERVICE_API_KEY
LLMSERVICE_BASEURL=$LLMSERVICE_BASEURL
WORKSPACE_PATH=/data
LlmService__claude_fable_default_model=qwen3.7-max
LlmService__claude_opus_default_model=qwen3.7-plus
LlmService__claude_sonnet_default_model=minimax-m3
LlmService__claude_haiku_default_model=qwen3.6-plus
LOG_TOKEN_FORMAT=true
EOF

# ── 5) Write the idempotent up-helper (used here AND by ~/.bashrc) ──────
cat > "$HELPER" <<'HELPER_EOF'
#!/usr/bin/env bash
# Ensure dockerd + the claude-proxy container are running. Safe to run
# repeatedly: starts dockerd ONLY if absent, starts the container if it
# exists (env baked in at create time), creates it only if missing.
#   proxy-up.sh ensure_docker   # just the daemon
#   proxy-up.sh ensure_proxy    # daemon + container  (default)
set -uo pipefail

PROXY_IMAGE="ghcr.io/generic-automation-and-it/smooth-claude-proxy:latest"
PROXY_DIR="$HOME/.claude/proxy"
ENV_FILE="$PROXY_DIR/proxy.env"

ensure_docker() {
  # No systemd in this sandbox (PID 1 is sandbox-init) — start dockerd by hand,
  # but ONLY if it isn't already serving. Never spawn a second daemon.
  if sudo docker info >/dev/null 2>&1; then return 0; fi
  sudo nohup dockerd >/tmp/dockerd.log 2>&1 &
  for _ in $(seq 1 30); do sudo docker info >/dev/null 2>&1 && return 0; sleep 1; done
  echo "WARN: dockerd not up — see /tmp/dockerd.log" >&2
  return 1
}

ensure_proxy() {
  ensure_docker || return 1
  # Already running? nothing to do.
  if [ "$(sudo docker inspect -f '{{.State.Running}}' claude-proxy 2>/dev/null)" = "true" ]; then
    return 0
  fi
  # Exists but stopped -> start it (env already baked in from create time).
  if sudo docker inspect claude-proxy >/dev/null 2>&1; then
    sudo docker start claude-proxy >/dev/null && return 0
  fi
  # Not present -> create it from the env file.
  sudo docker run -d --name claude-proxy --restart unless-stopped \
    -p 5066:5066 \
    -v "$PROXY_DIR:/data" \
    --env-file "$ENV_FILE" \
    "$PROXY_IMAGE" >/dev/null
}

"${1:-ensure_proxy}"
HELPER_EOF
chmod +x "$HELPER"

# ── 6) Bring the daemon up (once), then pull the image ──────────────────
"$HELPER" ensure_docker || echo "WARN: dockerd not up; runtime hook will retry."

# Optional: only needed if the GHCR package is PRIVATE. Provide a PAT with
# read:packages via GHCR_TOKEN (and GHCR_USER as your username).
if [ -n "${GHCR_TOKEN:-}" ]; then
  echo "$GHCR_TOKEN" | sudo docker login ghcr.io -u "${GHCR_USER:-x}" --password-stdin
fi

sudo docker pull "$PROXY_IMAGE" \
  || echo "WARN: image pre-pull failed; the ~/.bashrc hook will retry at runtime."

# ── 7) (Re)create the container from the freshly pulled image ───────────
# Force-recreate here so we always run the newest pulled image. The bashrc
# hook does NOT force-recreate; it only start-or-runs.
sudo docker rm -f claude-proxy >/dev/null 2>&1 || true
"$HELPER" ensure_proxy || echo "WARN: proxy container not started; runtime hook will retry."

# ── 8) Persist env + auto-start hook for future shells ──────────────────
if ! grep -q 'DOTNET_ROOT' "$HOME/.bashrc" 2>/dev/null; then
  {
    echo ''
    echo '# .NET'
    echo 'export DOTNET_ROOT="$HOME/.dotnet"'
    echo 'export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"'
  } >> "$HOME/.bashrc"
fi

if ! grep -q 'ANTHROPIC_BASE_URL' "$HOME/.bashrc" 2>/dev/null; then
  echo 'export ANTHROPIC_BASE_URL=http://localhost:5066' >> "$HOME/.bashrc"
fi

if ! grep -q 'proxy-up.sh' "$HOME/.bashrc" 2>/dev/null; then
  {
    echo ''
    echo '# Claude proxy: ensure dockerd + container are up (idempotent, fast no-op when healthy)'
    echo '[ -x "$HOME/.claude/proxy/proxy-up.sh" ] && "$HOME/.claude/proxy/proxy-up.sh" >/dev/null 2>&1'
  } >> "$HOME/.bashrc"
fi

if ! grep -q 'claude-yolo' "$HOME/.bashrc" 2>/dev/null; then
  echo "alias claude-yolo='claude --dangerously-skip-permissions'" >> "$HOME/.bashrc"
fi

# ── 9) Make THIS shell use the proxy immediately (no reopen needed) ─────
export ANTHROPIC_BASE_URL=http://localhost:5066

# ── 10) Verify ──────────────────────────────────────────────────────────
echo "── verify ─────────────────────────────────────────"
sudo docker ps --filter name=claude-proxy --format 'container: {{.Names}} {{.Status}}'
if sudo docker exec claude-proxy sh -c '[ -n "$LLMSERVICE_API_KEY" ]'; then
  echo "LLMSERVICE_API_KEY: present in container ✅"
else
  echo "LLMSERVICE_API_KEY: MISSING in container ❌ (check $ENV_FILE)"
fi
curl -fsS localhost:5066/health && echo || echo "health: not responding yet (give it a few seconds)"

echo "Setup complete. ANTHROPIC_BASE_URL is set for this shell and persisted for new shells."
```

## Verify routing end-to-end

After setup, from a Claude Code session pointed at the proxy:

```bash
curl -fsS localhost:5066/health            # {"status":"ok","target":"https://api.anthropic.com"}
curl -fsS localhost:5066/override-model    # shows Enabled + per-family model overrides
```

Then watch the container log for a routed request — you should see the family swap,
the OpenCode route, and a `200` passthrough:

```
sudo docker logs -f claude-proxy
# Model override: claude-sonnet-4-6 -> minimax-m3 (routing to LLM)
# -> POST /v1/messages [auth=API-Key, model=minimax-m3, route=OpenCode]
# LLM passthrough -> https://opencode.ai/zen/go/v1/messages [model=minimax-m3]
# <- 200 /v1/messages [LLM passthrough]
```
