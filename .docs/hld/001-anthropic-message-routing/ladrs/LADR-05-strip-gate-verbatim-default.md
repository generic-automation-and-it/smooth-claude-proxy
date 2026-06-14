# LADR-05: Verbatim-by-Default Strip Gate — Conversion Is Opt-In; Qwen Always Converts

**Status:** Accepted

## Context

The `openai` alternate dialect (LADR-04) *can* run a heavy Anthropic→OpenAI conversion-and-slimming pipeline: rewrite the model, drop Anthropic-specific fields, strip Claude Code system-reminder noise, slim tool definitions, and (for some models) rebuild the message array. This pipeline is powerful but **lossy and risky** — it re-serializes the body, makes assumptions about message structure, and can subtly change behaviour.

Many OpenAI-compatible upstreams tolerate an Anthropic-shaped body well enough (they ignore unknown fields). For those, running the pipeline is unnecessary risk. But some models — notably **Qwen** via its response handler — *require* the converted, non-streaming flow to work at all.

We need a default that favours safety, with explicit opt-in for the heavy path, and a hard exception for models that can't work without it.

## Decision

Gate the conversion pipeline on **`StripNonClaudeModels`** (default **off**), and define:

```
verbatim = (NOT StripNonClaudeModels) AND (NOT isQwen)
```

- **`verbatim` (default):** forward the request body to `/v1/chat/completions` **byte-for-byte** — no field conversion, no rewriting, no filtering — except an override model swap (LADR-03), which must still reach the upstream. Stream the reply straight back, untouched. Log a warning that an Anthropic-shaped body is being sent to an OpenAI endpoint, so an operator who hits a strict upstream knows to turn the gate on.
- **`StripNonClaudeModels` on:** run the full conversion + slimming pipeline, then translate the reply via a per-model response handler (LADR-08).
- **Qwen exception:** when the target model contains `qwen`, the pipeline **always** runs regardless of `StripNonClaudeModels`, because the Qwen response handler consumes a single non-streamed JSON body (`stream=false`) and cannot work from a verbatim passthrough.

The strip gate affects **only** the `openai` path. The `anthropic` passthrough (LADR-04) is unaffected by this setting.

Client-supplied `cache_control` is **never stripped** by the pipeline — it is forwarded untouched (most OpenAI-compatible servers ignore unknown fields; a strict one requires disabling `cache_control` client-side).

## Conversion pipeline (when it runs)

- **Fields dropped:** `model` (rewritten to the target), `budget_tokens`, `thinking`, `metadata`, `context_management`. For Qwen additionally: inbound `tool_choice`, `system`, `stream`.
- **Message text cleaned:** `<system-reminder>` blocks and other Claude Code infrastructure noise (`local-command-*`, `command-name`, `available-deferred-tools`) removed.
- **Tools slimmed:** reduced to name, first-line/truncated description, and a minimal parameter schema (descriptions kept only for required params).
- **Qwen-specific:** minimal fixed system prompt (Claude Code system blocks discarded); `tool_use`→`tool_calls`; `tool_result`→`tool` role messages (one per result); `tool_choice` forced to `required` when tools are present else `none`; `stream=false`.

## Alternatives Considered

- **Convert by default.** Rejected — imposes lossy translation on upstreams that don't need it and risks regressions on every request.
- **A per-model "needs conversion" flag instead of the Qwen substring check.** Reasonable future refinement; today Qwen is the only model requiring it, so the substring check is sufficient and avoids premature config surface.
- **Strip `cache_control` during conversion.** Rejected (and previously removed) — it breaks prompt caching for upstreams that honour it; ignoring an unknown field is the upstream's job.

## Consequences

- The safe path is the default; an operator opts into conversion only when their upstream rejects the Anthropic shape.
- Qwen "just works" without the operator needing to know it requires conversion.
- The verbatim warning gives a clear breadcrumb when a strict upstream returns an error.

## Related

- **LADR-04** — Dual API format (the strip gate lives inside the `openai` dialect).
- **LADR-06** — Cache-control handling (never stripped here; injected only on passthrough).
- **LADR-08** — Keyed response handlers (only the converted path uses them).
