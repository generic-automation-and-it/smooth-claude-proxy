# LADR-06: Prompt-Cache Injection on Anthropic Passthrough

**Status:** Accepted

## Context

Anthropic-compatible upstreams (opencode.ai Zen, MiniMax) support prompt caching via a `cache_control` marker, which can dramatically cut cost and latency on repeated large prompts. Claude Code, talking to *our* proxy, does not always emit `cache_control` — and even when it does, it places it block-level, not top-level.

On the `anthropic` passthrough path (LADR-04) we forward the body essentially unchanged, so if the client sent no cache marker, the upstream caches nothing. We'd like caching on by default for these upstreams, without overriding a client that *did* express its own caching intent.

## Decision

On the **`anthropic` passthrough path only**, if the request body contains **no `cache_control` anywhere**, append a **top-level** `cache_control: {"type": "ephemeral"}` to the forwarded body.

Detection is two-stage to be both fast and correct:

1. **Fast negative check** — a substring scan for `"cache_control"`. If absent, definitely none present.
2. **Structural confirmation** — if the substring is present, do a recursive structural search of the parsed JSON for an actual `cache_control` *property* (object key), because the substring could appear in prompt text that merely *mentions* `cache_control`.

Injection rules:

- Only when the body is a JSON **object**. Non-object bodies (arrays, scalars) are forwarded unchanged.
- Appended **after** the original properties so existing **key order is preserved**.
- A client-supplied `cache_control` (block-level or top-level) is **always forwarded untouched** — injection is suppressed whenever a real `cache_control` property exists anywhere.
- If a per-family model swap (LADR-03) also applies, both edits are made in the same single rewrite pass.

This injection happens **only** on the `anthropic` passthrough. The `openai` path does not inject (and does not strip) `cache_control`.

## JSON shape

Injected top-level property:

```json
{
  "cache_control": { "type": "ephemeral" }
}
```

## Alternatives Considered

- **Substring check only.** Rejected — false-positives on prompt text containing the literal `cache_control`, which would wrongly suppress injection. The structural confirm fixes this while the substring keeps the common (no-cache) case cheap.
- **Always inject (overwrite client intent).** Rejected — a client that set its own `cache_control` knows what it wants; clobbering it could change caching scope.
- **Inject on the `openai` path too.** Rejected — OpenAI Chat Completions has no standard top-level `cache_control`; injecting there is meaningless or harmful.
- **Re-serialize unconditionally to normalise.** Rejected — violates the transparency principle (LADR-01); only rewrite when there's a concrete edit to make.

## Consequences

- Anthropic-compatible upstreams get prompt caching automatically, lowering cost/latency on large repeated contexts, with no client change.
- Clients retain full control: any `cache_control` they send is honoured verbatim.
- The structural scan runs only when the substring is present, so the no-cache common case pays only for a substring search.

## Related

- **LADR-04** — Dual API format (injection is specific to the `anthropic` dialect).
- **LADR-05** — Strip gate (the `openai` path's non-stripping of `cache_control`).
- **LADR-01** — Transparency (rewrite only when there's an edit to make; preserve key order).
