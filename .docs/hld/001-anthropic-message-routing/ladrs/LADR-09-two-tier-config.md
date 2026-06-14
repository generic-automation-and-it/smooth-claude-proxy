# LADR-09: Two-Tier Configuration — Immutable Startup Options Seed Mutable Runtime Settings

**Status:** Accepted

## Context

Routing has two kinds of configuration with different lifecycles:

- **Connection-level, set-once-at-startup** — the alternate upstream's base URL and auth token, plus the initial values for the routing toggles. These come from `appsettings.json` and environment variables, must be available before the first request, and don't change while the process runs.
- **Behavioural, tweakable-at-runtime** — `Enabled`, `ApiFormat`, `StripNonClaudeModels`, and the per-family override targets. An operator wants to flip these live (e.g. enable conversion, change the dialect) without restarting the proxy and losing the connection config.

Mixing these into one mutable bag risks an operator accidentally changing the base URL at runtime, or losing the startup defaults when runtime settings are reset.

## Decision

Use **two tiers**:

1. **Startup options (immutable):** bound once from the standard configuration pipeline — `appsettings.json` plus a well-known **environment-variable bridge** that maps friendly env vars onto canonical config keys (only set vars are bridged, so appsettings remains the fallback). Holds the upstream `BaseUrl`, `AuthToken`, and the *initial* values for the routing toggles and per-family defaults. Read-only after startup.
2. **Runtime settings (mutable):** a settings object held in an in-memory cache, **seeded at startup from the immutable options**. This is what the decision core reads on every request, and what the `/override-model` endpoints mutate. Connection-level values (`BaseUrl`, `AuthToken`) are *not* part of this tier — they are read from the immutable options at the upstream call site.

The env-var bridge (canonical mapping):

| Environment variable | Canonical key | Meaning |
|---|---|---|
| `LMSTUDIO_BASE_URL` | upstream base URL | Alternate upstream address |
| `OPENCODE_API_KEY` (or legacy `LMSTUDIO_AUTH_TOKEN`) | upstream auth token | Alternate upstream credential |
| `CLAUDE_FABLE_DEFAULT_MODEL` | fable override | Per-family default (LADR-03) |
| `CLAUDE_OPUS_DEFAULT_MODEL` | opus override | Per-family default |
| `CLAUDE_SONNET_DEFAULT_MODEL` | sonnet override | Per-family default |
| `CLAUDE_HAIKU_DEFAULT_MODEL` | haiku override | Per-family default |

Precedence: env-var bridge values (when set) override appsettings values for the same key.

### Runtime settings JSON shape

```json
{
  "Enabled": true,
  "ApiFormat": "anthropic",
  "StripNonClaudeModels": false,
  "FableModel": null,
  "OpusModel": null,
  "SonnetModel": null,
  "HaikuModel": null
}
```

### `/override-model` contract

- `GET /override-model` — returns the current runtime settings plus the resolved upstream target (`BaseUrl`).
- `POST /override-model` — partial update; only `Enabled`, `ApiFormat`, and `StripNonClaudeModels` are mutable via this endpoint. Body shape:
  ```json
  { "Enabled": true, "ApiFormat": "openai", "StripNonClaudeModels": true }
  ```
  Any field omitted is left unchanged.
- `DELETE /override-model` — resets runtime settings to defaults (`Enabled=true`, `ApiFormat=anthropic`, `StripNonClaudeModels=false`).

## Alternatives Considered

- **One mutable config object.** Rejected — risks runtime mutation of connection-level values and loses the startup baseline on reset.
- **Reload everything from `appsettings.json` on change.** Rejected — file reload is coarser than the targeted live toggles operators want and complicates the runtime mutation story.
- **Expose `BaseUrl`/`AuthToken` via `/override-model`.** Rejected — these are deployment concerns; changing the upstream address at runtime is out of scope and a foot-gun.

## Consequences

- Connection config is locked at startup; behavioural config is live-tweakable, with a clean reset to startup-seeded defaults.
- Friendly env vars work without operators learning the canonical config-section keys.
- The decision core reads only the runtime settings tier; the upstream call site reads `BaseUrl`/`AuthToken` from the immutable tier.

## Related

- **LADR-03** — Per-family overrides (their targets live in both tiers — seeded from startup, mutable at rest).
- **LADR-04** / **LADR-05** — `ApiFormat` and `StripNonClaudeModels` are the runtime-mutable toggles.
