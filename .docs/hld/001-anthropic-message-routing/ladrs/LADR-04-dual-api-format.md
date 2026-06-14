# LADR-04: Dual Upstream API Format — `anthropic` Passthrough vs `openai` Conversion

**Status:** Accepted

## Context

Alternate upstreams come in two protocol flavours. Some speak the **Anthropic Messages API** natively (`POST /v1/messages`, SSE with `content_block_*` events, `tool_use`/`tool_result` blocks) — e.g. opencode.ai Zen, MiniMax. Others speak the **OpenAI Chat Completions API** (`POST /v1/chat/completions`, `choices[].message`, `tool_calls`) — e.g. LM Studio, local Qwen.

Claude Code only ever produces Anthropic-shaped requests and only understands Anthropic SSE. So for an Anthropic-native upstream we need (almost) no translation, while for an OpenAI upstream we need full bidirectional translation. The router must know which dialect the chosen upstream speaks.

## Decision

A single setting, **`ApiFormat`**, selects the alternate-route dialect:

- **`anthropic`** — *passthrough*. The inbound body is already in the right shape. Forward it to `{base}{path}{query}` (preserving the inbound path, e.g. `/v1/messages`) with only surgical edits: optional per-family model swap (LADR-03) and optional prompt-cache injection (LADR-06). Stream the SSE response straight back. **No format conversion in either direction.**
- **`openai`** — *conversion*. Forward to `{base}/v1/chat/completions`. The request is either forwarded verbatim or fully converted to OpenAI shape (gated by LADR-05), and a successful reply is translated back to Anthropic SSE by a per-model response handler (LADR-08).

`ApiFormat` is a single global value in the runtime settings (LADR-09). The default is `anthropic` — the lower-risk dialect, requiring no conversion.

Authentication differs per dialect: the `anthropic` passthrough sets `Authorization: Bearer`, `x-api-key`, and `anthropic-version` to the configured upstream credentials; the `openai` path sets `Authorization: Bearer` only. The inbound client token is never sent to an alternate upstream (see LADR-13).

## Alternatives Considered

- **Auto-detect the dialect from the upstream URL or a probe.** Rejected — fragile; the same base URL can serve both shapes, and a startup probe adds latency and a failure mode. An explicit setting is predictable.
- **Always convert to OpenAI shape.** Rejected — needless lossy translation for upstreams that already speak Anthropic; passthrough preserves prompt caching, tool fidelity, and unknown fields.
- **Always passthrough (assume Anthropic).** Rejected — OpenAI-only upstreams (the local Qwen use case) would reject the body.

## Consequences

- Adding an Anthropic-native upstream is near-zero work: point the base URL at it, set `ApiFormat=anthropic`, supply a token.
- Adding an OpenAI upstream requires conversion (LADR-05) and a response handler (LADR-08).
- `ApiFormat` is global, so all alternate traffic shares one dialect at a time. Supporting two alternate dialects simultaneously is a documented future migration (per-destination config), not a current capability.

## Related

- **LADR-05** — The strip gate (within `openai`, verbatim vs convert).
- **LADR-06** — Prompt-cache injection (within `anthropic`).
- **LADR-08** — Keyed response handlers (translate the `openai` reply back).
- **LADR-11** — Both dialects bypass YARP and use a direct HttpClient.
