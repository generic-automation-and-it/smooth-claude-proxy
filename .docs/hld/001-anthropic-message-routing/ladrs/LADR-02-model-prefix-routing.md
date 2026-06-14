# LADR-02: Model-Prefix Dispatch — `claude-*` → Anthropic, Else → Alternate

**Status:** Accepted

## Context

The router must decide, per request, whether to forward to Anthropic or to an alternate upstream. Earlier iterations tried richer dispatch: routing by a configured "from model", by prompt content, and by explicit from/to model mapping (see project AGENTS.md changelog, 2026-03-14 and 2026-06-07). These were brittle — content-based routing is non-deterministic and hard to reason about, and from/to mappings drifted out of sync with the models Claude Code actually emits.

We need a dispatch primitive that is **deterministic, inspectable from a single field, and requires no per-model configuration to get the common case right**.

## Decision

Dispatch on the **`model` field prefix**:

- A `model` that starts with `claude-` (case-insensitive) routes to **Anthropic**.
- Any other `model` routes to the **alternate upstream**.

This is evaluated as `routesToAnthropic = model.StartsWith("claude-")`, on the `model` value *after* per-family override resolution (LADR-03). The master switch for taking the alternate route is:

```
isLlmRoute = Enabled
             AND model is non-empty
             AND (NOT routesToAnthropic OR an override was applied)
```

No "from model", no prompt inspection, no mapping table for the base decision. The body's `model` field is the contract.

The first user prompt *is* extracted during body parsing, but only for logging/observability — it is **not** a routing input. Do not reintroduce prompt-based dispatch.

## Alternatives Considered

- **Prompt/content-based routing.** Rejected — non-deterministic, expensive to evaluate, impossible to predict from the outside. Removed in 2026-06-07.
- **Explicit from→to model mapping table.** Rejected as the *primary* mechanism — drifts against the model names Claude Code emits and needs maintenance for every model. Per-family overrides (LADR-03) cover the legitimate redirect use case with prefix matching instead of exact mapping.
- **Header-based routing (e.g. a custom `x-route-to` header).** Rejected — Claude Code doesn't emit it, so it would require a client change, violating LADR-01.

## Consequences

- Routing is a one-line, testable predicate. An operator can predict the destination of any request by reading its `model`.
- Because the decision keys on the post-override `model`, the override feature composes cleanly: change the model, and the same predicate naturally redirects it.
- A model field that is absent or empty cannot take the alternate route (`model is non-empty` guard) — such a request falls through to Anthropic.

## Related

- **LADR-03** — Per-family override (the only sanctioned way to redirect a `claude-*` family).
- **LADR-04** — Dual API format (what happens *after* the alternate route is chosen).
- **LADR-01** — Transparent insertion (the `claude-*` branch is the inert path).
