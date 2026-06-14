# Anthropic Message Routing — Implementation Plan

| | |
|---|---|
| **Version** | 0.1.0 |
| **Status** | Accepted (describes implemented behaviour; usable as a from-scratch reconstruction spec) |
| **Last updated** | 2026-06-14 |
| **HLD** | [`../README.md`](../README.md) |
| **AI context** | [`../ANTHROPIC_MESSAGE_ROUTING_HLD_AGENTS.md`](../ANTHROPIC_MESSAGE_ROUTING_HLD_AGENTS.md) |

---

## TL;DR

Build the routing subsystem as a single forwarding middleware whose decision core dispatches on the inbound `model` prefix. Deliver in four phases: **(1)** the decision core + transparent Anthropic passthrough; **(2)** the `anthropic` alternate dialect (passthrough + prompt-cache injection + `count_tokens` interception); **(3)** the `openai` dialect verbatim mode; **(4)** the `openai` converted pipeline + keyed response handlers. Configuration (two-tier) and streaming discipline are cross-cutting and land in Phase 1. Each phase is independently testable against Claude Code.

This is the tactical layer. Decisions are in the LADRs (`../ladrs/`).

---

## Component Overview

```
Claude Code (ANTHROPIC_BASE_URL → proxy)
        │  POST /v1/messages | POST */count_tokens
        ▼
┌─────────────────────────────────────────────────────────┐
│ Forwarding Middleware                                     │
│   1. Extract identity + headers (strip x-user-label)      │
│   2. If Enabled & JSON: parse model + first user prompt   │
│   3. Resolve per-family override (swap model if matched)  │
│   4. Compute routesToAnthropic / isLlmRoute               │
│   5. Dispatch ▼                                           │
└───┬──────────────┬───────────────┬───────────────────────┘
    │ Anthropic     │ alternate      │ alternate */count_tokens
    │ (YARP)        │ (HttpClient)   │ (local estimate)
    ▼               ▼                ▼
api.anthropic.com   ApiFormat?       {"input_tokens": n}
(+ usage tracking)  ├─ anthropic → {base}{path}{query} (+cache inject)
                    └─ openai    → {base}/v1/chat/completions
                                    ├─ verbatim → stream back
                                    └─ converted → handler → Anthropic SSE
```

---

## Configuration surface (lands in Phase 1)

Two tiers (LADR-09):

- **Immutable startup options** — bound from `appsettings.json` + env-var bridge. Holds `BaseUrl`, `AuthToken`, and initial toggle/override values.
- **Mutable runtime settings** — seeded from the options into an in-memory cache; read per request; mutated via `/override-model`.

| Env var | Canonical key | Default (appsettings) |
|---|---|---|
| `LMSTUDIO_BASE_URL` | `LlmService:BaseUrl` | `https://opencode.ai/zen/go` |
| `OPENCODE_API_KEY` / `LMSTUDIO_AUTH_TOKEN` | `LlmService:AuthToken` | `""` |
| — | `LlmService:Enabled` | `true` |
| — | `LlmService:ApiFormat` | `anthropic` |
| — | `LlmService:StripNonClaudeModels` | `false` |
| `CLAUDE_FABLE_DEFAULT_MODEL` | `LlmService:claude_fable_default_model` | `""` |
| `CLAUDE_OPUS_DEFAULT_MODEL` | `LlmService:claude_opus_default_model` | `""` |
| `CLAUDE_SONNET_DEFAULT_MODEL` | `LlmService:claude_sonnet_default_model` | `""` |
| `CLAUDE_HAIKU_DEFAULT_MODEL` | `LlmService:claude_haiku_default_model` | `""` |

Runtime settings JSON shape and `/override-model` contract are specified in [LADR-09](../ladrs/LADR-09-two-tier-config.md).

---

## Non-Functional Requirements

| Category | Requirement | Verification |
|---|---|---|
| **Transparency** | The Anthropic path makes no wire-level change to body or headers (beyond stripping proxy-only headers). Body is not parsed when routing is disabled. | Byte-diff a captured `claude-*` request through the proxy vs direct; confirm identical. Confirm no body read when `Enabled=false`. |
| **Streaming** | First SSE byte reaches the client as the upstream produces it on every path; buffering disabled. | Stream a long generation; observe incremental deltas in the CLI, not a single late burst. |
| **Latency** | Routing decision adds <1 ms; no full-response materialization on the hot path (Qwen `stream=false` is the documented exception). | Micro-benchmark the decision core; trace upstream-to-client first-byte. |
| **Long requests** | Upstream calls tolerate ≥10-minute generations on every path. | Timeout config = 10 min (YARP `ActivityTimeout` and HttpClient `Timeout`). |
| **Determinism** | Destination is a pure function of `Enabled`, `model` (post-override), `ApiFormat`, `StripNonClaudeModels`. | Table-driven tests over the decision matrix (see Acceptance gates). |
| **Open-closed** | A new OpenAI-compatible model needs only a handler registration + config, no decision-core change. | Add a stub handler; confirm routing reaches it without editing dispatch. |
| **Error clarity** | Failures map to 502 / 400 / 501 / relayed status with Anthropic-shaped bodies (LADR-12). | Fault-injection tests per condition. |
| **Tracking isolation** | Usage recorded only on the Anthropic non-session path. | Assert no DB write / rate-limit capture on alternate routes and in session mode. |

---

## Phase 1 — Decision Core + Transparent Anthropic Passthrough

**Goal.** Claude Code talks to the proxy and reaches real Anthropic, transparently, with usage tracking — routing always resolves to Anthropic in this phase.
**Exit criterion.** A full Claude Code session works through the proxy indistinguishably from direct; usage is recorded.

| # | Unit | Delivers |
|---|---|---|
| 1 | **Two-tier config** | Immutable startup options (appsettings + env bridge); mutable runtime settings seeded into in-memory cache; `/override-model` GET/POST/DELETE. (LADR-09) |
| 2 | **Identity & header extraction** | Auth-type detection, JWT email/name decode (no signature check), `x-user-label` capture+strip, `anthropic-version` capture. (LADR-13) |
| 3 | **Decision core (Anthropic-only)** | Body parse gated on `Enabled`+JSON; extract `model` + first user prompt; compute `routesToAnthropic`/`isLlmRoute`. In this phase every request resolves to the Anthropic path. (LADR-02) |
| 4 | **YARP catch-all + streaming** | `{**catch-all}` → `api.anthropic.com`, headers preserved, 10-min activity timeout; response buffering disabled. Endpoints registered before `MapReverseProxy`. (LADR-01, LADR-10, LADR-11) |
| 5 | **Usage tracking tail** | Capture `anthropic-ratelimit-unified-*`; write `UserRecord` via non-blocking channel — only when bearer present and no active session. (LADR-13) |

**Acceptance gates:** byte-identical passthrough; SSE streams incrementally; usage rows appear for `claude-*` traffic; no body read when `Enabled=false`.

---

## Phase 2 — `anthropic` Alternate Dialect

**Goal.** Route non-`claude-*` (and overridden) models to an Anthropic-compatible upstream, with caching and `count_tokens` handled.
**Exit criterion.** A non-Claude model request is served by the alternate upstream end-to-end with streaming SSE; `count_tokens` no longer 400s.

| # | Unit | Delivers |
|---|---|---|
| 1 | **Per-family override resolution** | Prefix match (fable/opus/sonnet/haiku); swap `model` field (key-order-preserving); set `overrideApplied`. (LADR-03) |
| 2 | **`count_tokens` interceptor** | On the alternate route, short-circuit `*/count_tokens` with `{"input_tokens": max(1000, len/4)}`. (LADR-07) |
| 3 | **Anthropic passthrough** | Direct HttpClient to `{base}{path}{query}`; upstream auth (`Authorization`+`x-api-key`+`anthropic-version`); stream SSE back. (LADR-04, LADR-11) |
| 4 | **Prompt-cache injection** | Substring fast-check → structural confirm; inject top-level ephemeral `cache_control` when none present; preserve key order; object-only; never override client `cache_control`. Combine with model swap in one rewrite pass. (LADR-06) |
| 5 | **Error mapping (passthrough)** | 502 on connection failure; relay upstream non-2xx; ≥400 warning log. (LADR-12) |

**Acceptance gates:** non-Claude model streams from the alternate upstream; overridden `claude-*` family routes to the alternate with the swapped model; `cache_control` injected only when absent; client `cache_control` preserved; `count_tokens` returns an estimate.

---

## Phase 3 — `openai` Dialect, Verbatim Mode

**Goal.** Support OpenAI-compatible upstreams without conversion (the safe default).
**Exit criterion.** With `ApiFormat=openai` and `StripNonClaudeModels=false`, a non-Claude request is forwarded to `/v1/chat/completions` and the reply streamed straight back.

| # | Unit | Delivers |
|---|---|---|
| 1 | **Verbatim forward** | `verbatim = !Strip && !isQwen`; POST body unchanged to `{base}/v1/chat/completions` with `Authorization: Bearer` upstream token; apply override model swap only; warn that an Anthropic-shape body is being sent. (LADR-05) |
| 2 | **Verbatim stream-back** | Stream the upstream response straight to the client, untouched. (LADR-10) |
| 3 | **Error mapping (openai)** | 502 unreachable; 400 on context-overflow signature; relay other non-2xx; 500 only on unexpected exception before response starts. (LADR-12) |

**Acceptance gates:** verbatim path streams; override swap reaches the upstream; context-overflow upstream error becomes a 400 `/compact` envelope.

---

## Phase 4 — `openai` Converted Pipeline + Keyed Response Handlers

**Goal.** Full Anthropic→OpenAI conversion for upstreams/models that require it (e.g. Qwen), with reply translation back to Anthropic SSE.
**Exit criterion.** With `StripNonClaudeModels=on` (or a Qwen model), a request is converted, sent, and the reply translated to a well-formed Anthropic SSE stream.

| # | Unit | Delivers |
|---|---|---|
| 1 | **Request conversion + slimming** | Rewrite model; drop `budget_tokens`/`thinking`/`metadata`/`context_management`; strip `<system-reminder>`/noise from message text; slim tools to name/desc/params. (LADR-05) |
| 2 | **Qwen conversion specifics** | Minimal fixed system prompt; `tool_use`→`tool_calls`; `tool_result`→`tool` role; `tool_choice` required/none; `stream=false`; drop inbound `tool_choice`/`system`/`stream`. Qwen always converts. (LADR-05) |
| 3 | **Keyed response-handler interface** | Resolve a handler by exact model name; handler emits Anthropic SSE (`message_start`…`message_stop`) flushing per event; `stop_reason=tool_use` when tool calls present. (LADR-08, LADR-10) |
| 4 | **Missing-handler 501** | No registered handler → explicit Anthropic-shaped 501 with remediation message; never an opaque 500. (LADR-08, LADR-12) |
| 5 | **Reference Qwen 2.5 handler** | Read full non-streamed JSON; emit text block + one `tool_use` per `tool_calls` entry (arguments via `input_json_delta`); correct `stop_reason`. (LADR-08) |

**Acceptance gates:** converted Qwen flow yields a valid Anthropic SSE stream usable by Claude Code (text + tool calls); unsupported converted model returns 501 with guidance; `stop_reason` correct.

---

## Decision Matrix (test oracle)

Drive Phase-by-phase acceptance from this table; the destination is a pure function of these inputs (post-override `model`).

| `Enabled` | `model` | override applied | `ApiFormat` | `Strip`/Qwen | path = `count_tokens` | Expected |
|---|---|---|---|---|---|---|
| false | any | — | — | — | — | Anthropic (no body parse) |
| true | `claude-sonnet-…` | no | — | — | no | Anthropic |
| true | `claude-haiku-…` (haiku override set) | yes | anthropic | — | no | Alternate passthrough (model swapped) |
| true | `qwen…` | — | openai | Qwen | no | Alternate converted (handler) |
| true | `some-openai-model` | — | openai | Strip off | no | Alternate verbatim |
| true | `some-openai-model` | — | openai | Strip on | no | Alternate converted (handler or 501) |
| true | `some-model` | — | anthropic | — | no | Alternate passthrough |
| true | non-Claude | — | any | — | yes | Local `count_tokens` estimate |

---

## Risks & Open Questions

### Risks

| ID | Risk | Mitigation |
|---|---|---|
| R1 | Re-serialization on rewrite paths reorders keys or drops fields, breaking caching or fidelity. | Rewrite passes preserve key order and copy unknown fields; object-only guard; the inert Anthropic path never re-serializes. |
| R2 | A buffered path silently hangs the CLI. | Streaming discipline is a non-negotiable (LADR-10); every direct path uses header-read completion + incremental copy/flush. |
| R3 | Converted path emits malformed SSE for a new model. | Handler interface mandates the full event sequence; missing handler is 501, not silent garbage. |
| R4 | Global `ApiFormat` can't serve two alternate dialects at once. | Documented migration to per-destination config; flag rather than overload. |

### Open questions

- **Streaming the converted path:** should a streaming OpenAI upstream handler consume upstream SSE incrementally (vs the current Qwen `stream=false` read)? Interface already permits it.
- **Per-model `ApiFormat`/`Strip`:** if more models need divergent handling, lift these from global to per-model/per-destination.
- **Override family set:** generalise the four enumerated families to a prefix→override map if families proliferate.

---

## Versioning

| Version | Date | Change |
|---|---|---|
| 0.1.0 | 2026-06-14 | Initial plan. Four phases, two-tier config, NFRs, decision-matrix test oracle, risks/open questions. Reflects implemented routing behaviour. |
