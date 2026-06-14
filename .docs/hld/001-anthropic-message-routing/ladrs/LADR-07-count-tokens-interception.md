# LADR-07: `count_tokens` Interception for Alternate Routes

**Status:** Accepted

## Context

Claude Code calls the Anthropic `count_tokens` endpoint (`POST /v1/messages/count_tokens`) to size its own context window before sending a request. Real Anthropic implements it; **alternate upstreams generally do not**. When an alternate upstream is the target and Claude Code calls `count_tokens`, forwarding the call returns a 400 (endpoint unknown).

The downstream effect is severe and non-obvious: with `count_tokens` failing, Claude Code can't track context size, eventually sends an oversized request, the upstream silently truncates it, and the model loses conversation history — manifesting as the assistant looping on the same commands. The failure mode looks like a model-quality problem, not a routing problem.

## Decision

On the **alternate route** (`isLlmRoute` true), intercept any request whose path ends with `count_tokens` (case-insensitive) and answer it **locally**, without calling any upstream:

```
estimated_tokens = max(1000, body_length_in_bytes / 4)
```

Return HTTP 200 with the Anthropic-shaped body:

```json
{ "input_tokens": 1234 }
```

The estimate is deliberately crude (≈4 bytes/token, floored at 1000). Its purpose is to let Claude Code's context-management heuristic function — not to be exact. Interception applies regardless of `ApiFormat`.

## Alternatives Considered

- **Forward `count_tokens` to the alternate upstream.** Rejected — most return 400; the ones that don't use incompatible response shapes.
- **Run a real tokenizer.** Rejected — would require shipping and matching each upstream model's tokenizer; far more cost and dependency than the use case (context-window sizing) needs. The 4-bytes/token heuristic is adequate.
- **Return a fixed large number.** Rejected — too coarse; a body-length-proportional estimate tracks growth, which is what the context heuristic actually consumes.

## Consequences

- Claude Code can manage its context window on alternate routes, avoiding the silent-truncation-and-loop failure.
- The estimate is approximate; it can over- or under-count relative to the upstream's real tokenizer, which is acceptable for context sizing.
- The interception short-circuits before any dialect handling, so it is identical for `anthropic` and `openai` formats.

## Related

- **LADR-02** — Model-prefix dispatch (interception only fires on the alternate route).
- **LADR-04** — Dual API format (interception precedes the dialect branch).
