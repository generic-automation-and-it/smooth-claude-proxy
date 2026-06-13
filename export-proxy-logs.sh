#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────
#  Export the local Claude proxy logs and transfer them to .context.
#  Run this on the remote machine where the proxy runs (terminal + proxy
#  are on the same host, so "transfer" is a local copy — no scp needed).
# ──────────────────────────────────────────────────────────────────────

# Destination (override by passing a path as $1)
DEST="${1:-/home/vercel-sandbox/workspace/.context}"

# Where the proxy writes its logs: $WORKSPACE_PATH/logs, else the default.
WORKSPACE_PATH="${WORKSPACE_PATH:-$HOME/.claude/proxy}"
SRC="$WORKSPACE_PATH/logs"

# Fall back to the conventional location if the env-derived one is empty.
if [ ! -d "$SRC" ] || [ -z "$(ls -A "$SRC" 2>/dev/null)" ]; then
  if [ -d "$HOME/.claude/proxy/logs" ]; then
    SRC="$HOME/.claude/proxy/logs"
  fi
fi

if [ ! -d "$SRC" ]; then
  echo "ERROR: no proxy log directory found (looked in $SRC)" >&2
  echo "       set WORKSPACE_PATH to the proxy's workspace and retry." >&2
  exit 1
fi

mkdir -p "$DEST"

# Timestamp for this export (sandbox blocks Date.now() in JS, not in shell).
STAMP="$(date +%Y%m%d-%H%M%S)"

# 1) Bundle the whole log dir into a single timestamped tarball in .context.
ARCHIVE="$DEST/proxy-logs-$STAMP.tar.gz"
tar -czf "$ARCHIVE" -C "$SRC" .

# 2) Also drop a fresh copy of the raw .log files into .context/proxy-logs/
RAW="$DEST/proxy-logs"
mkdir -p "$RAW"
cp -f "$SRC"/*.log "$RAW"/ 2>/dev/null || true

# 3) Include the nohup stdout log if present (startup/crash output).
[ -f "$HOME/proxy.log" ] && cp -f "$HOME/proxy.log" "$RAW/proxy-stdout.log"

echo "Source : $SRC"
echo "Bundle : $ARCHIVE"
echo "Raw    : $RAW/"
echo
echo "Exported files:"
ls -la "$RAW"
