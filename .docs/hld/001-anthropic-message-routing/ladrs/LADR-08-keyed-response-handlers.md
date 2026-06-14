# LADR-08: Keyed Per-Model Response Handlers — Open-Closed, Explicit 501 When Missing

**Status:** Accepted

## Context

On the converted `openai` path (LADR-05), the upstream returns an OpenAI Chat Completions reply, but Claude Code only understands Anthropic SSE. The reply must be translated: `choices[].message.content` → Anthropic text content blocks, `tool_calls` → `tool_use` blocks, and the whole thing re-emitted as a `message_start` … `content_block_*` … `message_stop` SSE sequence with the correct `stop_reason`.

Different models reply in different enough ways that one translator can't cover them all robustly (an earlier attempt at a generic "Liquid" handler was removed because it didn't handle tool calls reliably). We need translation to be **per-model and extensible**, and the failure when a model has no translator must be **clear and actionable**, not an opaque crash.

## Decision

Define a **response-handler interface** keyed by **exact model name**. Each supported model registers its own handler in the composition root (e.g. a Qwen 2.5 handler). At response time, the router resolves the handler for the target model:

- **Handler found** → it consumes the upstream reply and writes Anthropic SSE to the client.
- **No handler registered** → return an explicit **501 Not Implemented** with an Anthropic-shaped error envelope whose message tells the operator exactly what to do (register a handler, switch to `ApiFormat=anthropic` passthrough, or disable `StripNonClaudeModels`). The keyed lookup must **never** be allowed to throw an opaque 500.

Adding support for a new OpenAI-compatible model is therefore **open-closed**: write and register a handler; no change to the decision core.

The handler interface contract (conceptual):

- **Inputs:** the HTTP context (to write the response), the upstream `HttpResponseMessage`, the target model name, a logger.
- **Output:** Anthropic-compatible SSE written to the response body; buffering already disabled by the caller.
- **Obligation:** emit a well-formed event sequence — `message_start`, then per content block `content_block_start`/`content_block_delta`/`content_block_stop`, then `message_delta` (with `stop_reason` = `tool_use` when any tool call was emitted, else `end_turn`) and `message_stop`.

## Reference: Qwen 2.5 handler behaviour

The Qwen handler reads the **full non-streamed** JSON reply (its request set `stream=false`, per LADR-05), then:

- Extracts `choices[0].message.content` (text) and `choices[0].message.tool_calls`.
- Emits a `message_start`, then a text content block if content is present, then one `tool_use` block per tool call (id preserved or generated; arguments emitted via `input_json_delta`).
- Sets `stop_reason` to `tool_use` if any tool calls were present, else `end_turn`.

## Alternatives Considered

- **One generic OpenAI→Anthropic translator.** Rejected — model reply quirks (especially tool-call encoding) defeated a single translator (the removed Liquid handler).
- **Throw on missing handler.** Rejected — yields an opaque 500 that looks like a proxy bug; a 501 with guidance is diagnosable.
- **Fall back to verbatim streaming when no handler exists.** Rejected for the *converted* path — a converted (e.g. `stream=false`) request produces a non-SSE body that Claude Code can't read; failing loudly is safer than emitting garbage. (Verbatim streaming is the *separate*, deliberate default per LADR-05.)

## Consequences

- New models are added by registration, not by editing routing logic.
- A misconfiguration (converted path, unsupported model) produces a precise, actionable error instead of a confusing failure.
- Each handler owns its own SSE fidelity; correctness is per-model and independently testable.

## Related

- **LADR-05** — Strip gate (only the converted path reaches a handler; Qwen forces it).
- **LADR-04** — Dual API format (handlers exist only for the `openai` dialect).
- **LADR-12** — Anthropic-shaped errors (the 501 envelope).
